using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Cluster;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>
/// Settings that can change while the app runs: manual mode, reviewers added on the fly, reviewer names,
/// the texts of the bot's Slack messages, the start and end of the event, the channels that got the
/// game-start and game-end messages, whether attempts after approval are blocked, when the jury is reminded,
/// and whether groups see the standing (see RuntimeSettingsService.Standings.cs).
/// They are kept in the database (<see cref="IRuntimeSettingsStore"/>), shared by every instance of the app: a change on
/// one instance reaches the others through <see cref="IClusterBus"/>.
/// </summary>
public partial class RuntimeSettingsService
{
    public const int MaxNameLength = 60;

    /// <summary>Well below Slack's own limit, and long enough for any instruction to the groups.</summary>
    public const int MaxSlackTextLength = 4000;

    /// <summary>The longest wait that can be set for a reminder: four hours.</summary>
    public const int MaxReminderMinutes = 240;

    /// <summary>Event times outside these years are refused: they are typing mistakes, and dates near year 1 cannot be converted between time zones.</summary>
    public static readonly DateTime MinEventTime = new(2000, 1, 1);
    public static readonly DateTime MaxEventTime = new(2100, 1, 1);

    /// <summary>Whether an event time can be used; null (no time) can.</summary>
    public static bool IsValidEventTime(DateTime? value) => value is not { } v || (v >= MinEventTime && v < MaxEventTime);

    private readonly object _lock = new();
    private readonly IRuntimeSettingsStore _store;
    private readonly IClusterBus _bus;
    private long _version;
    private bool? _manualMode;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<RuntimeSettingsService> _logger;
    private readonly ConcurrentDictionary<string, byte> _manualDownloads = new();
    // Filled by Load in the constructor
    private List<string> _addedReviewers = new();
    private Dictionary<string, SavedName> _names = new();
    private Dictionary<string, DateTimeOffset> _gameStartPosted = new();
    private Dictionary<string, DateTimeOffset> _gameEndPosted = new();
    private TimeOverride? _eventStart;
    private TimeOverride? _eventEnd;
    private Dictionary<SlackText, string> _texts = new();
    private bool? _blockAttempts;
    private ReminderOverride? _reminder;

    /// <param name="store">Where the settings are kept; in memory when left out (tests).</param>
    /// <param name="bus">Tells the other instances about a change, and reloads when they made one.</param>
    public RuntimeSettingsService(IOptions<ScoutingSettings> options, ILogger<RuntimeSettingsService> logger,
        IRuntimeSettingsStore? store = null, IClusterBus? bus = null)
    {
        _options = options;
        _logger = logger;
        _store = store ?? new InMemoryRuntimeSettingsStore();
        _bus = bus ?? SingleInstance.Instance;
        try
        {
            lock (_lock) Load();
        }
        catch (Exception ex)
        {
            // Made before the database is set up, or while it cannot be reached: DatabaseInitializer loads the settings once
            // the table is there. A database problem must not stop the app
            _logger.LogDebug(ex, "Settings made in the app are loaded after the database is set up");
        }

        _bus.Subscribe(ClusterTopic, _ => Reload());
        _bus.Reconnected += Reload;
    }

    /// <summary>The message the other instances get when the settings changed.</summary>
    public const string ClusterTopic = "settings";

    /// <summary>Reads the settings again, after another instance changed them or the database was set up.</summary>
    public void Reload()
    {
        try
        {
            lock (_lock) Load();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read the settings from the database; the ones in memory stay");
            return;
        }

        Changed?.Invoke();
    }

    /// <summary>Raised when any setting of this service changes, from any thread.</summary>
    public event Action? Changed;

    /// <summary>Set in the app, otherwise from appsettings. Kept like the other settings, so all instances agree.</summary>
    public bool ManualMode
    {
        get { lock (_lock) return _manualMode ?? _options.Value.ManualMode; }
    }

    /// <summary>The start of the event as a point in time: set in the app, otherwise from appsettings; null when there is none.</summary>
    // Converted on each read: the game's time zone is configured after this service may be created
    public DateTimeOffset? EventStart => EventStartGameTime is { } start ? GameTime.FromGameTime(start) : null;

    /// <summary>The end of the event as a point in time: set in the app, otherwise from appsettings; null when there is none.</summary>
    public DateTimeOffset? EventEnd => EventEndGameTime is { } end ? GameTime.FromGameTime(end) : null;

    /// <summary>The start of the event in the game's time zone, as it is set.</summary>
    public DateTime? EventStartGameTime
    {
        get { lock (_lock) return _eventStart != null ? _eventStart.Value : ValidOrNull(_options.Value.EventStart, "EventStart"); }
    }

    /// <summary>The end of the event in the game's time zone, as it is set.</summary>
    public DateTime? EventEndGameTime
    {
        get { lock (_lock) return _eventEnd != null ? _eventEnd.Value : ValidOrNull(_options.Value.EventEnd, "EventEnd"); }
    }

    private DateTime? ValidOrNull(DateTime? value, string setting)
    {
        if (IsValidEventTime(value)) return value;

        _logger.LogError("Scouting:{Setting} {Value} is not between {Min} and {Max}; it is ignored", setting, value, MinEventTime.Year, MaxEventTime.Year);
        return null;
    }

    /// <summary>Whether the start of the event was changed in the app.</summary>
    public bool IsEventStartChanged
    {
        get { lock (_lock) return _eventStart != null; }
    }

    /// <summary>Whether the end of the event was changed in the app.</summary>
    public bool IsEventEndChanged
    {
        get { lock (_lock) return _eventEnd != null; }
    }

    /// <summary>Sets the start of the event in the app, in the game's time zone; null means no start time.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetEventStart(DateTime? start) => SetEventTime(start, isEnd: false);

    /// <summary>Sets the end of the event in the app, in the game's time zone; null means no end time.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetEventEnd(DateTime? end) => SetEventTime(end, isEnd: true);

    /// <summary>Goes back to the start time from appsettings.</summary>
    public void ResetEventStart() => ResetEventTime(isEnd: false);

    /// <summary>Goes back to the end time from appsettings.</summary>
    public void ResetEventEnd() => ResetEventTime(isEnd: true);

    private string? SetEventTime(DateTime? value, bool isEnd)
    {
        // Seconds are not shown in the app; a stray value would make the boundary unclear
        value = value is { } v ? DateTime.SpecifyKind(v.AddTicks(-(v.Ticks % TimeSpan.TicksPerMinute)), DateTimeKind.Unspecified) : null;
        if (!IsValidEventTime(value))
        {
            return $"Kies een tijd tussen {MinEventTime.Year} en {MaxEventTime.Year}.";
        }

        lock (_lock)
        {
            var start = isEnd ? EventStartGameTime : value;
            var end = isEnd ? value : EventEndGameTime;
            if (start is { } s && end is { } e && e <= s)
            {
                return "De eindtijd moet na de starttijd liggen.";
            }

            if (isEnd) _eventEnd = new TimeOverride { Value = value };
            else _eventStart = new TimeOverride { Value = value };
            Save();
        }

        _logger.LogWarning("Event {Kind} set to {Time} in the app", isEnd ? "end" : "start", value?.ToString("yyyy-MM-dd HH:mm") ?? "none");
        Changed?.Invoke();
        return null;
    }

    private void ResetEventTime(bool isEnd)
    {
        lock (_lock)
        {
            if ((isEnd ? _eventEnd : _eventStart) == null) return;
            if (isEnd) _eventEnd = null;
            else _eventStart = null;
            Save();
        }

        _logger.LogWarning("Event {Kind} reset to appsettings", isEnd ? "end" : "start");
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether a Slack message was posted before the event started. A message without a valid timestamp
    /// is never treated as early, so a live message cannot be lost.
    /// </summary>
    public bool IsBeforeEventStart(string? ts) => IsBeforeStart(ts, adventurers: false);

    /// <summary>
    /// Whether a Slack message was posted before its part of the game started: the Avonturiers part with
    /// <paramref name="adventurers"/>, otherwise the event. A message without a valid timestamp is never treated as early.
    /// </summary>
    public bool IsBeforeStart(string? ts, bool adventurers) =>
        StartOf(adventurers) is { } start && SlackTimestamp.ToDateTimeOffset(ts) is { } postedAt && postedAt < start;

    /// <summary>
    /// Whether a Slack message was posted at or after the end of the event. A message without a valid timestamp
    /// is never treated as late, so a live message cannot be lost.
    /// </summary>
    public bool IsAfterEventEnd(string? ts) => IsAfterEnd(ts, adventurers: false);

    /// <summary>
    /// Whether a Slack message was posted at or after the end of its part of the game: the Avonturiers part with
    /// <paramref name="adventurers"/>, otherwise the event. A message without a valid timestamp is never treated as late.
    /// </summary>
    public bool IsAfterEnd(string? ts, bool adventurers) =>
        EndOf(adventurers) is { } end && SlackTimestamp.ToDateTimeOffset(ts) is { } postedAt && postedAt >= end;

    /// <summary>The start of the Avonturiers part with <paramref name="adventurers"/>, otherwise of the event.</summary>
    public DateTimeOffset? StartOf(bool adventurers) => adventurers ? AdventurersStartAt : EventStart;

    /// <summary>The end of the Avonturiers part with <paramref name="adventurers"/>, otherwise of the event.</summary>
    public DateTimeOffset? EndOf(bool adventurers) => adventurers ? AdventurersFinalAt : EventEnd;

    /// <summary>
    /// The later of <paramref name="since"/> and the earliest start: of the event, or of the Avonturiers part when that
    /// starts earlier. A part without a start time keeps <paramref name="since"/>.
    /// </summary>
    public DateTimeOffset NotBeforeEventStart(DateTimeOffset since)
    {
        var starts = AdventurersEnabled ? new[] { EventStart, AdventurersStartAt } : new[] { EventStart };
        if (starts.Any(s => s == null)) return since;

        var earliest = starts.Min()!.Value;
        return earliest > since ? earliest : since;
    }

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
    public string DisplayName(string? userId) => userId switch
    {
        // Stored values that are not Slack users; they stay as they are so older data still matches
        SubmissionWorkflowService.DashboardOverrideReviewer => "Handmatig aangepast",
        SubmissionWorkflowService.CatchUpReviewer => "Bijgewerkt uit Slack",
        _ => NameOf(userId) ?? userId ?? ""
    };

    /// <summary>Whether the name was entered by hand, which a fetch from Slack never overwrites.</summary>
    public bool HasManualName(string userId)
    {
        lock (_lock) return _names.TryGetValue(userId.Trim(), out var saved) && !saved.FromSlack;
    }

    public void SetManualMode(bool enabled)
    {
        lock (_lock)
        {
            if (ManualMode == enabled) return;

            _manualMode = enabled;
            Save();
        }

        _logger.LogWarning("Manual mode switched {State}", enabled ? "on" : "off");
        Changed?.Invoke();
    }

    /// <returns>Null when added, otherwise why not.</returns>
    public string? AddReviewer(string? userId)
    {
        var id = userId?.Trim() ?? "";
        if (!ReviewerIdPattern().IsMatch(id))
        {
            return "Vul een Slack-member-ID in: alleen letters en cijfers, zoals U04ABC123.";
        }

        lock (_lock)
        {
            if (IsReviewer(id))
            {
                return $"{id} is al jurylid.";
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
            return $"{userId} is geen jurylid.";
        }

        if (trimmed.Length > MaxNameLength)
        {
            return $"Een naam mag maximaal {MaxNameLength} tekens zijn.";
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

    /// <summary>The text of a Slack message from appsettings.</summary>
    public string? ConfiguredText(SlackText kind)
    {
        var settings = _options.Value;
        var feedback = settings.SlackFeedback ?? new SlackFeedbackConfig();
        return kind switch
        {
            SlackText.GameStart => settings.GameStartAnnouncement?.Message,
            SlackText.GameEnd => settings.GameEndAnnouncement?.Message,
            SlackText.UnsupportedFile => feedback.UnsupportedFile?.Message,
            SlackText.MissingAssignmentNumber => feedback.MissingAssignmentNumber?.Message,
            SlackText.UnknownAssignmentNumber => feedback.UnknownAssignmentNumber?.Message,
            SlackText.DownloadFailed => feedback.DownloadFailed?.Message,
            SlackText.AlreadyCompleted => feedback.AlreadyCompleted?.Message,
            SlackText.TooLate => feedback.TooLate?.Message,
            _ when IsAdventurersText(kind) => ConfiguredAdventurersText(kind),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }

    /// <summary>The text the bot posts: the one set in the app, otherwise the one from appsettings. Empty means nothing is posted.</summary>
    public string? Text(SlackText kind)
    {
        lock (_lock)
        {
            if (_texts.TryGetValue(kind, out var text)) return text;
        }

        return ConfiguredText(kind);
    }

    /// <summary>Whether the text was changed in the app.</summary>
    public bool IsTextChanged(SlackText kind)
    {
        lock (_lock) return _texts.ContainsKey(kind);
    }

    /// <summary>
    /// The feedback from appsettings, with the texts changed in the app. The emoji always come from appsettings.
    /// </summary>
    public SlackFeedbackConfig Feedback
    {
        get
        {
            var configured = _options.Value.SlackFeedback ?? new SlackFeedbackConfig();
            return new SlackFeedbackConfig
            {
                UnsupportedFile = WithText(configured.UnsupportedFile, SlackText.UnsupportedFile),
                MissingAssignmentNumber = WithText(configured.MissingAssignmentNumber, SlackText.MissingAssignmentNumber),
                UnknownAssignmentNumber = WithText(configured.UnknownAssignmentNumber, SlackText.UnknownAssignmentNumber),
                DownloadFailed = WithText(configured.DownloadFailed, SlackText.DownloadFailed),
                AlreadyCompleted = WithText(configured.AlreadyCompleted, SlackText.AlreadyCompleted),
                TooLate = WithText(configured.TooLate, SlackText.TooLate)
            };
        }
    }

    private FeedbackConfig WithText(FeedbackConfig? configured, SlackText kind) =>
        new() { Reaction = configured?.Reaction, Message = Text(kind) };

    /// <summary>Sets the text of a Slack message in the app; an empty text means nothing is posted.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetText(SlackText kind, string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length > MaxSlackTextLength)
        {
            return $"Een bericht mag maximaal {MaxSlackTextLength} tekens zijn.";
        }

        lock (_lock)
        {
            _texts[kind] = value;
            Save();
        }

        _logger.LogWarning("Slack text {Kind} changed in the app", kind);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the text from appsettings.</summary>
    public void ResetText(SlackText kind)
    {
        lock (_lock)
        {
            if (!_texts.Remove(kind)) return;
            Save();
        }

        _logger.LogWarning("Slack text {Kind} reset to appsettings", kind);
        Changed?.Invoke();
    }

    /// <summary>
    /// Whether a new attempt for an assignment the group already has approved or awesome is blocked:
    /// set in the app, otherwise from appsettings.
    /// </summary>
    public bool BlockAttemptsAfterApproval
    {
        get
        {
            lock (_lock) return _blockAttempts ?? _options.Value.BlockAttemptsAfterApproval;
        }
    }

    /// <summary>Whether <see cref="BlockAttemptsAfterApproval"/> was switched in the app.</summary>
    public bool IsBlockAttemptsChanged
    {
        get { lock (_lock) return _blockAttempts.HasValue; }
    }

    public void SetBlockAttemptsAfterApproval(bool enabled)
    {
        lock (_lock)
        {
            if (_blockAttempts == enabled) return;
            _blockAttempts = enabled;
            Save();
        }

        _logger.LogWarning("Blocking attempts after approval switched {State} in the app", enabled ? "on" : "off");
        Changed?.Invoke();
    }

    /// <summary>Goes back to the setting from appsettings.</summary>
    public void ResetBlockAttemptsAfterApproval()
    {
        lock (_lock)
        {
            if (_blockAttempts == null) return;
            _blockAttempts = null;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The jury channel for reminders from appsettings; null when there is none.</summary>
    public string? ReminderChannelId =>
        string.IsNullOrWhiteSpace(_options.Value.ReviewReminder?.ChannelId) ? null : _options.Value.ReviewReminder.ChannelId.Trim();

    /// <summary>
    /// After how many minutes of waiting the jury is reminded: set in the app, otherwise from appsettings.
    /// Null means no reminders.
    /// </summary>
    public int? ReviewReminderMinutes
    {
        get
        {
            lock (_lock)
            {
                if (_reminder != null) return _reminder.Minutes;
            }

            return _options.Value.ReviewReminder?.AfterMinutes is int minutes && minutes > 0 ? minutes : null;
        }
    }

    /// <summary>Whether the reminder time was changed in the app.</summary>
    public bool IsReviewReminderChanged
    {
        get { lock (_lock) return _reminder != null; }
    }

    /// <summary>Sets the reminder time in the app; null switches reminders off.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetReviewReminderMinutes(int? minutes)
    {
        if (minutes is < 1 or > MaxReminderMinutes)
        {
            return $"Kies tussen 1 en {MaxReminderMinutes} minuten, of laat het leeg om geen herinneringen te sturen.";
        }

        lock (_lock)
        {
            _reminder = new ReminderOverride { Minutes = minutes };
            Save();
        }

        _logger.LogWarning("Review reminder set to {Minutes} minutes in the app", minutes?.ToString() ?? "off");
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the reminder time from appsettings.</summary>
    public void ResetReviewReminder()
    {
        lock (_lock)
        {
            if (_reminder == null) return;
            _reminder = null;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>When the game-start message was posted in a channel; null when it was not.</summary>
    public DateTimeOffset? GameStartPostedAt(string channelId)
    {
        lock (_lock) return _gameStartPosted.TryGetValue(channelId, out var at) ? at : null;
    }

    /// <summary>Remembers that the game-start message was posted in a channel, so a restart does not post it again.</summary>
    public void MarkGameStartPosted(string channelId, DateTimeOffset postedAt)
    {
        lock (_lock)
        {
            SaveRecord(() => _gameStartPosted[channelId] = postedAt);
        }

        Changed?.Invoke();
    }

    /// <summary>When the game-end message was posted in a channel; null when it was not.</summary>
    public DateTimeOffset? GameEndPostedAt(string channelId)
    {
        lock (_lock) return _gameEndPosted.TryGetValue(channelId, out var at) ? at : null;
    }

    /// <summary>Remembers that the game-end message was posted in a channel, so a restart does not post it again.</summary>
    public void MarkGameEndPosted(string channelId, DateTimeOffset postedAt)
    {
        lock (_lock)
        {
            SaveRecord(() => _gameEndPosted[channelId] = postedAt);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Claims one file of one message for a manual-mode download. Slack can deliver an event twice;
    /// only the first claim downloads. Kept in memory: redeliveries come within minutes.
    /// </summary>
    public bool TryClaimManualDownload(string channel, string messageTs, string fileName) =>
        _manualDownloads.TryAdd($"{channel}|{messageTs}|{fileName}", 0);

    /// <summary>Forgets a claim of <see cref="TryClaimManualDownload"/>, after the download failed, so it is tried again.</summary>
    public void ReleaseManualDownload(string channel, string messageTs, string fileName) =>
        _manualDownloads.TryRemove($"{channel}|{messageTs}|{fileName}", out _);

    private void Load()
    {
        // Read first: when the database cannot be reached, the settings in memory stay as they were
        var (json, version) = _store.Load();
        Apply(json, version);
    }

    /// <summary>The settings as last read from or saved to the database, to fall back on when a save fails.</summary>
    private (string? Json, long Version) _persisted;

    private void Apply(string? json, long version)
    {
        _persisted = (json, version);
        ResetToDefaults();
        _version = version;
        if (json == null) return;

        try
        {
            var saved = JsonSerializer.Deserialize<SavedSettings>(json);
            _manualMode = saved?.ManualMode;
            _addedReviewers = saved?.Reviewers?.Where(r => ReviewerIdPattern().IsMatch(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();
            foreach (var (id, name) in saved?.ReviewerNames ?? new Dictionary<string, SavedName>())
            {
                if (!string.IsNullOrWhiteSpace(name?.Name)) _names[id] = name;
            }

            foreach (var (channel, at) in saved?.GameStartPosted ?? new Dictionary<string, DateTimeOffset>())
            {
                _gameStartPosted[channel] = at;
            }

            foreach (var (channel, at) in saved?.GameEndPosted ?? new Dictionary<string, DateTimeOffset>())
            {
                _gameEndPosted[channel] = at;
            }

            // A time that cannot be used would stop every message and the app's start: it is dropped
            _eventStart = IsValidEventTime(saved?.EventStart?.Value) ? saved?.EventStart : null;
            _eventEnd = IsValidEventTime(saved?.EventEnd?.Value) ? saved?.EventEnd : null;
            if (_eventStart != saved?.EventStart || _eventEnd != saved?.EventEnd)
            {
                _logger.LogError("The saved settings have an event time outside {Min}-{Max}; the time from appsettings is used", MinEventTime.Year, MaxEventTime.Year);
            }

            foreach (var (key, text) in saved?.SlackTexts ?? new Dictionary<string, string>())
            {
                // A text of a kind this version does not know is skipped
                if (Enum.TryParse<SlackText>(key, out var kind) && text != null) _texts[kind] = text;
            }

            _blockAttempts = saved?.BlockAttemptsAfterApproval;
            // An out-of-range value in a hand-edited file falls back to appsettings
            if (saved?.ReviewReminder is { } reminder && reminder.Minutes is null or (>= 1 and <= MaxReminderMinutes))
            {
                _reminder = reminder;
            }

            LoadStandings(saved?.Standings);
            LoadBonusCategories(saved?.BonusCategories);
            LoadAdventurers(saved?.Adventurers);
        }
        catch (JsonException ex)
        {
            // Damaged settings must not stop the app; the reviewers from appsettings still apply.
            // Logged in full, so what was in them can still be recovered before the next save overwrites them.
            _logger.LogError(ex, "Could not read the saved settings; settings made in the app are not loaded. They were: {Json}", json);
            ResetToDefaults();
        }
    }

    private void ResetToDefaults()
    {
        _addedReviewers = new List<string>();
        _names = new Dictionary<string, SavedName>(StringComparer.OrdinalIgnoreCase);
        _gameStartPosted = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        _gameEndPosted = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        _texts = new Dictionary<SlackText, string>();
        _eventStart = null;
        _eventEnd = null;
        _blockAttempts = null;
        _reminder = null;
        _manualMode = null;
        ResetStandings();
        _bonusCategories = null;
        LoadAdventurers(null);
    }

    /// <summary>
    /// Writes the settings to the store. When that fails (database down, or another instance saved in between), the
    /// settings in memory go back to what is stored and <see cref="RuntimeSettingsSaveException"/> is thrown: the app never
    /// shows a change that is not kept.
    /// </summary>
    private void Save()
    {
        var settings = new SavedSettings { Reviewers = _addedReviewers, ReviewerNames = _names, GameStartPosted = _gameStartPosted,
            GameEndPosted = _gameEndPosted, EventStart = _eventStart, EventEnd = _eventEnd,
            SlackTexts = _texts.ToDictionary(t => t.Key.ToString(), t => t.Value),
            BlockAttemptsAfterApproval = _blockAttempts, ReviewReminder = _reminder, Standings = SaveStandings(),
            BonusCategories = _bonusCategories, Adventurers = SaveAdventurers(), ManualMode = _manualMode };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        long? saved;
        try
        {
            saved = _store.TrySave(json, _version);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save the settings; the change is undone");
            TryLoadAfterFailedSave();
            throw new RuntimeSettingsSaveException("de database is niet bereikbaar.", ex);
        }

        if (saved == null)
        {
            _logger.LogWarning("The settings were changed elsewhere at the same moment; the change is undone");
            TryLoadAfterFailedSave();
            throw new RuntimeSettingsSaveException("de instellingen zijn net ergens anders gewijzigd. Kijk of je wijziging nog nodig is en probeer het opnieuw.", changedElsewhere: true);
        }

        _version = saved.Value;
        _persisted = (json, saved.Value);
        _bus.Publish(ClusterTopic);
    }

    /// <summary>
    /// Saves a record of something the app already did (a message posted): unlike a change by an admin, that must not be
    /// lost because another instance saved other settings at the same moment, so it is applied again on the new settings.
    /// Call while holding <c>_lock</c>.
    /// </summary>
    private void SaveRecord(Action apply)
    {
        while (true)
        {
            apply();
            try
            {
                Save();
                return;
            }
            catch (RuntimeSettingsSaveException ex) when (ex.ChangedElsewhere)
            {
                // Save read the newer settings back; apply the record to those. Each attempt starts from the newest
                // version, so it only fails again when yet another save came in between: it does not give up and lose
                // the record. A database that cannot be reached still ends the attempts.
            }
        }
    }

    private void TryLoadAfterFailedSave()
    {
        try
        {
            Load();
        }
        catch (Exception ex)
        {
            // The failed change must not stay in effect: back to the settings as last saved
            _logger.LogError(ex, "Could not read the settings back after a failed save; the last saved ones apply");
            Apply(_persisted.Json, _persisted.Version);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{2,50}$")]
    private static partial Regex ReviewerIdPattern();

    private sealed class SavedSettings
    {
        public List<string>? Reviewers { get; set; }
        public Dictionary<string, SavedName>? ReviewerNames { get; set; }
        public Dictionary<string, DateTimeOffset>? GameStartPosted { get; set; }
        public Dictionary<string, DateTimeOffset>? GameEndPosted { get; set; }
        public TimeOverride? EventStart { get; set; }
        public TimeOverride? EventEnd { get; set; }
        public Dictionary<string, string>? SlackTexts { get; set; }
        public bool? BlockAttemptsAfterApproval { get; set; }
        public ReminderOverride? ReviewReminder { get; set; }
        public SavedStandings? Standings { get; set; }
        public List<BonusCategoryConfig>? BonusCategories { get; set; }
        public SavedAdventurers? Adventurers { get; set; }
        public bool? ManualMode { get; set; }
    }

    /// <summary>A reminder time set in the app; <see cref="Minutes"/> null means reminders are off.</summary>
    private sealed class ReminderOverride
    {
        public int? Minutes { get; set; }
    }

    /// <summary>A start or end time set in the app, in the game's time zone; <see cref="Value"/> null means none.</summary>
    private sealed class TimeOverride
    {
        public DateTime? Value { get; set; }
    }

    private sealed class SavedName
    {
        public string Name { get; set; } = "";
        public bool FromSlack { get; set; }
    }
}

/// <summary>The Slack messages whose text an admin can change in the app.</summary>
public enum SlackText
{
    GameStart,
    GameEnd,
    UnsupportedFile,
    MissingAssignmentNumber,
    UnknownAssignmentNumber,
    DownloadFailed,
    AlreadyCompleted,
    TooLate,
    AdventurersStart,
    AdventurersFinal,
    RoundOpen,
    RoundClose,
    RoundResult,
    RoundHandover,
    UnlockNotice,
    UnlockCorrection,
    AdventurersMissingNumber,
    UnknownLocation,
    UnknownTheme,
    ThemeOutsideRound,
    NoRoundOpen,
    LocationNotUnlocked,
    AssignmentLocked
}
