using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// The shared access key and the admin key. Each key has a stamp that changes when the key is regenerated,
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

        if (changed) Save();
    }

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
            _keys.AccessKey = key;
            _keys.AccessStamp = NewStamp();
            Save();
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
            _keys.AdminKey = key;
            _keys.AdminStamp = NewStamp();
            Save();
        }

        _logger.LogWarning("Admin key regenerated; other admins lose admin mode");
        Changed?.Invoke();
        return key;
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
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

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
            return JsonSerializer.Deserialize<SavedKeys>(File.ReadAllText(_file)) ?? new SavedKeys();
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Could not read {File}; new keys are created", _file);
            return new SavedKeys();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_keys, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }

    private sealed class SavedKeys
    {
        public string AccessKey { get; set; } = "";
        public string AccessStamp { get; set; } = "";
        public string AdminKey { get; set; } = "";
        public string AdminStamp { get; set; } = "";
    }
}
