using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// The shared access key, the admin key and a key per group. Each key has a stamp that changes when the key is regenerated,
/// so logins made with an older key stop working. Keys are saved, so a restart never brings back an old key.
/// </summary>
public class AccessKeyService
{
    // No 0/O or 1/I: keys are read aloud and typed over
    private const string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    private readonly object _lock = new();
    private readonly string _file;
    private readonly ILogger<AccessKeyService> _logger;
    private SavedKeys _keys;

    public AccessKeyService(IOptions<ScoutingSettings> options, ILogger<AccessKeyService> logger)
    {
        _logger = logger;
        _file = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.KeysFile) ? "./access-keys.json" : options.Value.KeysFile);
        _keys = Load();

        var changed = false;
        if (string.IsNullOrWhiteSpace(_keys.AccessKey))
        {
            _keys.AccessKey = FirstKey(options.Value.AccessKey, "access");
            _keys.AccessStamp = NewStamp();
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(_keys.AdminKey))
        {
            _keys.AdminKey = FirstKey(options.Value.AdminKey, "admin");
            _keys.AdminStamp = NewStamp();
            changed = true;
        }

        if (changed) Save(_keys);
        DurableFile.RestrictToOwner(_file, _logger);
    }

    /// <summary>A key set in the configuration should be at least this long; a shorter one is easy to guess.</summary>
    public const int MinConfiguredKeyLength = 12;

    /// <summary>Raised when a key is regenerated, from any thread.</summary>
    public event Action? Changed;

    /// <summary>The key to share with the jury and organisers.</summary>
    public string AccessKey
    {
        get { lock (_lock) return _keys.AccessKey; }
    }

    public string AccessStamp
    {
        get { lock (_lock) return _keys.AccessStamp; }
    }

    public string AdminStamp
    {
        get { lock (_lock) return _keys.AdminStamp; }
    }

    public bool IsAccessKey(string? key) => Matches(key, AccessKey);

    public bool IsAdminKey(string? key)
    {
        lock (_lock) return Matches(key, _keys.AdminKey);
    }

    /// <summary>Creates a new access key; everyone logged in with the old one is logged out.</summary>
    public string RegenerateAccessKey()
    {
        string key;
        lock (_lock)
        {
            key = Generate();
            // Saved first: when saving fails, the old key stays in use and the admin sees the error
            var keys = _keys with { AccessKey = key, AccessStamp = NewStamp() };
            Save(keys);
            _keys = keys;
        }

        _logger.LogWarning("Access key regenerated; everyone else is logged out");
        Changed?.Invoke();
        return key;
    }

    /// <summary>Creates a new admin key; every other admin loses admin mode.</summary>
    public string RegenerateAdminKey()
    {
        string key;
        lock (_lock)
        {
            key = Generate();
            var keys = _keys with { AdminKey = key, AdminStamp = NewStamp() };
            Save(keys);
            _keys = keys;
        }

        _logger.LogWarning("Admin key regenerated; other admins lose admin mode");
        Changed?.Invoke();
        return key;
    }

    /// <summary>The key of the group with this Slack channel; null when it has none yet.</summary>
    public string? GroupKey(string channelId)
    {
        lock (_lock) return _keys.GroupKeys.TryGetValue(channelId, out var key) ? key.Key : null;
    }

    /// <summary>The stamp of the group's key; null when it has none, so a login made with a removed key is no longer valid.</summary>
    public string? GroupStamp(string channelId)
    {
        lock (_lock) return _keys.GroupKeys.TryGetValue(channelId, out var key) ? key.Stamp : null;
    }

    /// <summary>The Slack channel of the group this key belongs to; null when it is no group key.</summary>
    public string? FindGroupChannel(string? key)
    {
        List<KeyValuePair<string, SavedGroupKey>> keys;
        lock (_lock) keys = _keys.GroupKeys.ToList();

        // Every key is compared, also after a match: the time taken does not tell which group matched
        string? found = null;
        foreach (var (channel, saved) in keys)
        {
            if (Matches(key, saved.Key) && found == null) found = channel;
        }

        return found;
    }

    /// <summary>Creates a new key for one group; whoever is logged in with its old key is logged out.</summary>
    public string RegenerateGroupKey(string channelId)
    {
        string key;
        lock (_lock)
        {
            key = Generate();
            var groupKeys = new Dictionary<string, SavedGroupKey>(_keys.GroupKeys, StringComparer.OrdinalIgnoreCase)
            {
                [channelId] = new() { Key = key, Stamp = NewStamp() }
            };
            var keys = _keys with { GroupKeys = groupKeys };
            Save(keys);
            _keys = keys;
        }

        _logger.LogWarning("Group key for channel {Channel} regenerated; that group is logged out", channelId);
        Changed?.Invoke();
        return key;
    }

    /// <summary>Gives every group in <paramref name="channelIds"/> without a key a new one.</summary>
    /// <returns>How many keys were made.</returns>
    public int EnsureGroupKeys(IEnumerable<string> channelIds)
    {
        int made;
        lock (_lock)
        {
            var groupKeys = new Dictionary<string, SavedGroupKey>(_keys.GroupKeys, StringComparer.OrdinalIgnoreCase);
            var missing = channelIds.Where(c => !string.IsNullOrWhiteSpace(c) && !groupKeys.ContainsKey(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (missing.Count == 0) return 0;

            foreach (var channel in missing)
            {
                groupKeys[channel] = new SavedGroupKey { Key = Generate(), Stamp = NewStamp() };
            }

            var keys = _keys with { GroupKeys = groupKeys };
            Save(keys);
            _keys = keys;
            made = missing.Count;
        }

        _logger.LogInformation("Made {Count} group keys", made);
        Changed?.Invoke();
        return made;
    }

    /// <summary>Removes the key of one group; whoever is logged in with it is logged out.</summary>
    public bool RemoveGroupKey(string channelId)
    {
        lock (_lock)
        {
            if (!_keys.GroupKeys.ContainsKey(channelId)) return false;
            var groupKeys = new Dictionary<string, SavedGroupKey>(_keys.GroupKeys, StringComparer.OrdinalIgnoreCase);
            groupKeys.Remove(channelId);
            var keys = _keys with { GroupKeys = groupKeys };
            Save(keys);
            _keys = keys;
        }

        _logger.LogWarning("Group key for channel {Channel} removed; that group is logged out", channelId);
        Changed?.Invoke();
        return true;
    }

    /// <summary>A random key like "7KQ4-M9TX-2HPW-C8RN" (80 bits).</summary>
    public static string Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, 16);
        return string.Join('-', Enumerable.Range(0, 4).Select(i => new string(chars, i * 4, 4)));
    }

    /// <summary>Keys are compared without case and spaces, in constant time.</summary>
    private static bool Matches(string? given, string expected)
    {
        if (string.IsNullOrWhiteSpace(given) || string.IsNullOrWhiteSpace(expected)) return false;

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(given))),
            SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(expected))));
    }

    private static string Normalize(string key) => key.Replace(" ", "").Trim().ToUpperInvariant();

    private string FirstKey(string? configured, string kind)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // Still used: refusing it would lock the organisers out on the day itself
            if (Normalize(configured).Length < MinConfiguredKeyLength)
            {
                _logger.LogWarning("The configured {Kind} key is shorter than {Length} characters and easy to guess; regenerate it on the Beheer page",
                    kind, MinConfiguredKeyLength);
            }

            return configured.Trim();
        }

        var key = Generate();
        // Shown once, so whoever starts the app can log in and share it
        _logger.LogWarning("Generated {Kind} key: {Key} (saved in {File})", kind, key, _file);
        return key;
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private SavedKeys Load()
    {
        if (!File.Exists(_file)) return new SavedKeys();

        try
        {
            var saved = JsonSerializer.Deserialize<SavedKeys>(File.ReadAllText(_file)) ?? new SavedKeys();
            // Older files have no group keys; channel IDs are compared without case, like everywhere else
            var groupKeys = (saved.GroupKeys ?? new Dictionary<string, SavedGroupKey>())
                .Where(k => !string.IsNullOrWhiteSpace(k.Value?.Key) && !string.IsNullOrWhiteSpace(k.Value.Stamp))
                .ToDictionary(k => k.Key, k => k.Value, StringComparer.OrdinalIgnoreCase);
            return saved with { GroupKeys = groupKeys };
        }
        catch (JsonException ex)
        {
            // Set aside, not overwritten: the keys in it can still be looked up
            var aside = DurableFile.Quarantine(_file, _logger);
            _logger.LogError(ex, "Could not read {File}; new keys are created{Aside}", _file, aside == null ? "" : $" (old file kept as {aside})");
            return new SavedKeys();
        }
    }

    private void Save(SavedKeys keys) =>
        DurableFile.WriteAllText(_file, JsonSerializer.Serialize(keys, new JsonSerializerOptions { WriteIndented = true }), ownerOnly: true);

    private sealed record SavedKeys
    {
        public string AccessKey { get; set; } = "";
        public string AccessStamp { get; set; } = "";
        public string AdminKey { get; set; } = "";
        public string AdminStamp { get; set; } = "";
        /// <summary>Per Slack channel of a group.</summary>
        public Dictionary<string, SavedGroupKey> GroupKeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SavedGroupKey
    {
        public string Key { get; set; } = "";
        public string Stamp { get; set; } = "";
    }
}
