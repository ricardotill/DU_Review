using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Settings that can change while the app runs: manual mode, reviewers added on the fly and reviewer names.
/// Manual mode starts from appsettings on every start; reviewers and names are saved to a file.
/// </summary>
public partial class RuntimeSettingsService
{
    public const int MaxNameLength = 60;

    private readonly object _lock = new();
    private readonly string _file;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<RuntimeSettingsService> _logger;
    private readonly ConcurrentDictionary<string, byte> _manualDownloads = new();
    private List<string> _addedReviewers;
    private Dictionary<string, SavedName> _names;

    public RuntimeSettingsService(IOptions<ScoutingSettings> options, ILogger<RuntimeSettingsService> logger)
    {
        _options = options;
        _logger = logger;
        _file = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.RuntimeSettingsFile) ? "./runtime-settings.json" : options.Value.RuntimeSettingsFile);
        ManualMode = options.Value.ManualMode;
        (_addedReviewers, _names) = Load();
    }

    /// <summary>Raised when manual mode, the reviewers or their names change, from any thread.</summary>
    public event Action? Changed;

    public bool ManualMode { get; private set; }

    /// <summary>Reviewers from appsettings; these can only be changed there.</summary>
    public IReadOnlyList<string> ConfiguredReviewers =>
        (_options.Value.Reviewers ?? new List<string>()).Select(r => r.Trim()).Where(r => r.Length > 0).ToList();

    /// <summary>Reviewers added in the app.</summary>
    public IReadOnlyList<string> AddedReviewers
    {
        get { lock (_lock) return _addedReviewers.ToList(); }
    }

    /// <summary>Every reviewer: from appsettings first, then those added in the app.</summary>
    public IReadOnlyList<string> AllReviewers => ConfiguredReviewers.Concat(AddedReviewers).ToList();

    public bool IsReviewer(string? user) =>
        !string.IsNullOrWhiteSpace(user)
        && AllReviewers.Any(r => r.Equals(user.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A reviewer's name: set in the app (by hand or fetched from Slack) before the one in appsettings.
    /// Null when there is none.
    /// </summary>
    public string? NameOf(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return null;

        lock (_lock)
        {
            if (_names.TryGetValue(userId.Trim(), out var saved)) return saved.Name;
        }

        var configured = _options.Value.ReviewerNames ?? new Dictionary<string, string>();
        var name = configured.FirstOrDefault(n => n.Key.Equals(userId.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>The name to show: the reviewer's name, or the ID itself when there is none.</summary>
    public string DisplayName(string? userId) => NameOf(userId) ?? userId ?? "";

    /// <summary>Whether the name was entered by hand, which a fetch from Slack never overwrites.</summary>
    public bool HasManualName(string userId)
    {
        lock (_lock) return _names.TryGetValue(userId.Trim(), out var saved) && !saved.FromSlack;
    }

    public void SetManualMode(bool enabled)
    {
        if (ManualMode == enabled) return;

        ManualMode = enabled;
        _logger.LogWarning("Manual mode switched {State}", enabled ? "on" : "off");
        Changed?.Invoke();
    }

    /// <returns>Null when added, otherwise why not.</returns>
    public string? AddReviewer(string? userId)
    {
        var id = userId?.Trim() ?? "";
        if (!ReviewerIdPattern().IsMatch(id))
        {
            return "Enter a Slack member ID: letters and digits only, such as U04ABC123.";
        }

        lock (_lock)
        {
            if (IsReviewer(id))
            {
                return $"{id} is already a reviewer.";
            }

            _addedReviewers = _addedReviewers.Append(id).ToList();
            Save();
        }

        _logger.LogWarning("Reviewer {Reviewer} added in the app", id);
        Changed?.Invoke();
        return null;
    }

    /// <returns>False for reviewers that were not added in the app.</returns>
    public bool RemoveReviewer(string userId)
    {
        lock (_lock)
        {
            var remaining = _addedReviewers.Where(r => !r.Equals(userId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (remaining.Count == _addedReviewers.Count) return false;

            _addedReviewers = remaining;
            _names.Remove(userId);
            Save();
        }

        _logger.LogWarning("Reviewer {Reviewer} removed in the app", userId);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sets a reviewer's name by hand; an empty name removes it again.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetReviewerName(string userId, string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (!IsReviewer(userId))
        {
            return $"{userId} is not a reviewer.";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"A name can be at most {MaxNameLength} characters.";
        }

        lock (_lock)
        {
            if (trimmed.Length == 0)
            {
                _names.Remove(userId);
            }
            else
            {
                _names[userId] = new SavedName { Name = trimmed, FromSlack = false };
            }

            Save();
        }

        Changed?.Invoke();
        return null;
    }

    /// <summary>Stores a name fetched from Slack, unless the name was entered by hand.</summary>
    /// <returns>True when the name was stored.</returns>
    public bool SetNameFromSlack(string userId, string name)
    {
        lock (_lock)
        {
            if (HasManualName(userId) || string.IsNullOrWhiteSpace(name)) return false;

            var trimmed = name.Trim();
            _names[userId] = new SavedName { Name = trimmed.Length > MaxNameLength ? trimmed[..MaxNameLength] : trimmed, FromSlack = true };
            Save();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Claims one file of one message for a manual-mode download. Slack can deliver an event twice;
    /// only the first claim downloads. Kept in memory: redeliveries come within minutes.
    /// </summary>
    public bool TryClaimManualDownload(string channel, string messageTs, string fileName) =>
        _manualDownloads.TryAdd($"{channel}|{messageTs}|{fileName}", 0);

    private (List<string> Reviewers, Dictionary<string, SavedName> Names) Load()
    {
        var names = new Dictionary<string, SavedName>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(_file)) return (new List<string>(), names);

        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(_file));
            var reviewers = saved?.Reviewers?.Where(r => ReviewerIdPattern().IsMatch(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            foreach (var (id, name) in saved?.ReviewerNames ?? new Dictionary<string, SavedName>())
            {
                if (!string.IsNullOrWhiteSpace(name?.Name)) names[id] = name;
            }

            return (reviewers, names);
        }
        catch (JsonException ex)
        {
            // A damaged file must not stop the app; the reviewers from appsettings still apply
            _logger.LogError(ex, "Could not read {File}; reviewers and names set in the app are not loaded", _file);
            return (new List<string>(), names);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temp = _file + ".tmp";
        var settings = new SavedSettings { Reviewers = _addedReviewers, ReviewerNames = _names };
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _file, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{2,50}$")]
    private static partial Regex ReviewerIdPattern();

    private sealed class SavedSettings
    {
        public List<string>? Reviewers { get; set; }
        public Dictionary<string, SavedName>? ReviewerNames { get; set; }
    }

    private sealed class SavedName
    {
        public string Name { get; set; } = "";
        public bool FromSlack { get; set; }
    }
}
