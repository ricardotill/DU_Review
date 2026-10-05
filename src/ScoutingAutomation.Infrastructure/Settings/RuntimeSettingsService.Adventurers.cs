using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>
/// The Avonturiers settings that can change while the app runs: the theme rounds, the unlock pool, the location check,
/// the times of the start and final messages, which channels got which message, and the unlock count each
/// Toeristen channel was last told. Whether the part is on at all only comes from appsettings.
/// </summary>
public partial class RuntimeSettingsService
{
    /// <summary>More rounds than this is a typing loop, not a game.</summary>
    public const int MaxRounds = 60;

    /// <summary>Most theme numbers in one round.</summary>
    public const int MaxThemesPerRound = 10;

    private List<ThemeRoundConfig>? _rounds;
    private List<string>? _unlockPool;
    private bool? _requireUnlockedLocation;
    private TimeOverride? _adventurersStartAt;
    private TimeOverride? _adventurersFinalAt;
    private Dictionary<string, Dictionary<string, DateTimeOffset>> _adventurersPosted = new();
    private Dictionary<Guid, int> _unlockNotified = new();

    public AdventurersSettings AdventurersOptions => _options.Value.Adventurers ?? new AdventurersSettings();

    /// <summary>Whether the Avonturiers part is on (appsettings only).</summary>
    public bool AdventurersEnabled => AdventurersOptions.Enabled;

    /// <summary>The feedback of the Avonturiers part, with the texts changed in the app. The emoji come from appsettings.</summary>
    public AdventurersFeedbackConfig AdventurersFeedback
    {
        get
        {
            var configured = AdventurersOptions.Feedback ?? new AdventurersFeedbackConfig();
            return new AdventurersFeedbackConfig
            {
                MissingNumber = WithText(configured.MissingNumber, SlackText.AdventurersMissingNumber),
                UnknownLocation = WithText(configured.UnknownLocation, SlackText.UnknownLocation),
                UnknownTheme = WithText(configured.UnknownTheme, SlackText.UnknownTheme),
                ThemeOutsideRound = WithText(configured.ThemeOutsideRound, SlackText.ThemeOutsideRound),
                NoRoundOpen = WithText(configured.NoRoundOpen, SlackText.NoRoundOpen),
                LocationNotUnlocked = WithText(configured.LocationNotUnlocked, SlackText.LocationNotUnlocked),
                AssignmentLocked = WithText(configured.AssignmentLocked, SlackText.AssignmentLocked)
            };
        }
    }

    private static bool IsAdventurersText(SlackText kind) => kind >= SlackText.AdventurersStart;

    private string? ConfiguredAdventurersText(SlackText kind)
    {
        var settings = AdventurersOptions;
        var feedback = settings.Feedback ?? new AdventurersFeedbackConfig();
        return kind switch
        {
            SlackText.AdventurersStart => settings.StartAnnouncement?.Message,
            SlackText.AdventurersFinal => settings.FinalAnnouncement?.Message,
            SlackText.RoundOpen => settings.RoundOpenMessage,
            SlackText.RoundClose => settings.RoundCloseMessage,
            SlackText.RoundResult => settings.RoundResultMessage,
            SlackText.UnlockNotice => settings.UnlockNoticeMessage,
            SlackText.UnlockCorrection => settings.UnlockCorrectionMessage,
            SlackText.AdventurersMissingNumber => feedback.MissingNumber?.Message,
            SlackText.UnknownLocation => feedback.UnknownLocation?.Message,
            SlackText.UnknownTheme => feedback.UnknownTheme?.Message,
            SlackText.ThemeOutsideRound => feedback.ThemeOutsideRound?.Message,
            SlackText.NoRoundOpen => feedback.NoRoundOpen?.Message,
            SlackText.LocationNotUnlocked => feedback.LocationNotUnlocked?.Message,
            SlackText.AssignmentLocked => feedback.AssignmentLocked?.Message,
            _ => null
        };
    }

    /// <summary>Whether the jury is warned about a check-in at a location not unlocked for the group: set in the app, otherwise from appsettings.</summary>
    public bool RequireUnlockedLocation
    {
        get { lock (_lock) return _requireUnlockedLocation ?? AdventurersOptions.RequireUnlockedLocation; }
    }

    public void SetRequireUnlockedLocation(bool enabled)
    {
        lock (_lock)
        {
            if (_requireUnlockedLocation == enabled) return;
            _requireUnlockedLocation = enabled;
            Save();
        }

        _logger.LogWarning("Location check for Avonturiers switched {State} in the app", enabled ? "on" : "off");
        Changed?.Invoke();
    }

    /// <summary>
    /// The Toeristen assignments in the order check-ins unlock them; empty means count mode (nothing is ever locked).
    /// Set in the app, otherwise from appsettings.
    /// </summary>
    public IReadOnlyList<string> UnlockPool
    {
        get
        {
            lock (_lock)
            {
                return (_unlockPool ?? (AdventurersOptions.UnlockPool ?? new List<string>()))
                    .Select(AssignmentCode.Normalize).OfType<string>().Distinct().ToList();
            }
        }
    }

    /// <summary>Whether the pool was changed in the app.</summary>
    public bool IsUnlockPoolChanged
    {
        get { lock (_lock) return _unlockPool != null; }
    }

    /// <summary>Sets the pool from a list such as "3, 5, 12, A"; an empty list switches to count mode.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetUnlockPool(IEnumerable<string> codes)
    {
        var list = new List<string>();
        foreach (var raw in codes.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var code = AssignmentCode.Normalize(raw);
            if (code == null) return $"'{raw.Trim()}' is geen opdrachtnummer.";
            if (list.Contains(code)) return $"Opdracht {code} staat twee keer in de lijst.";
            list.Add(code);
        }

        lock (_lock)
        {
            _unlockPool = list;
            Save();
        }

        _logger.LogWarning("Unlock pool set to {Pool} in the app", list.Count == 0 ? "count mode" : string.Join(", ", list));
        Changed?.Invoke();
        return null;
    }

    public void ResetUnlockPool()
    {
        lock (_lock)
        {
            if (_unlockPool == null) return;
            _unlockPool = null;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The theme rounds, earliest first, with their times as points in time.</summary>
    public IReadOnlyList<ThemeRound> ThemeRounds
    {
        get
        {
            List<ThemeRoundConfig> rounds;
            lock (_lock) rounds = _rounds ?? AdventurersOptions.Rounds ?? new List<ThemeRoundConfig>();
            var minutes = RoundMinutes;
            return rounds
                .Where(r => r != null && CheckRound(r, minutes) == null)
                .Select(r => ToRound(r, minutes))
                .OrderBy(r => r.Start)
                .ToList();
        }
    }

    /// <summary>The rounds as they are set, for editing.</summary>
    public IReadOnlyList<ThemeRoundConfig> RoundConfigs
    {
        get
        {
            lock (_lock)
            {
                return (_rounds ?? AdventurersOptions.Rounds ?? new List<ThemeRoundConfig>())
                    .Select(CopyRound).OrderBy(r => r.Start).ToList();
            }
        }
    }

    /// <summary>Whether the rounds were changed in the app.</summary>
    public bool IsRoundsChanged
    {
        get { lock (_lock) return _rounds != null; }
    }

    /// <summary>The length of a generated round, and of a round without an end.</summary>
    public int RoundMinutes => AdventurersOptions.RoundMinutes is > 0 and <= 24 * 60 ? AdventurersOptions.RoundMinutes : 30;

    /// <summary>Replaces all rounds. They may not overlap, and a theme number belongs to one round only.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetRounds(IEnumerable<ThemeRoundConfig> rounds)
    {
        var list = rounds.Select(CopyRound).ToList();
        if (CheckRounds(list, RoundMinutes) is { } error) return error;

        lock (_lock)
        {
            _rounds = list.OrderBy(r => r.Start).ToList();
            Save();
        }

        _logger.LogWarning("{Count} theme round(s) set in the app", list.Count);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the rounds from appsettings.</summary>
    public void ResetRounds()
    {
        lock (_lock)
        {
            if (_rounds == null) return;
            _rounds = null;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Rounds of <paramref name="minutes"/> from <paramref name="from"/> until <paramref name="to"/>, numbered from 1, with
    /// theme N in round N. Only whole rounds: 12:00 to 16:30 with 30 minutes gives 9 rounds.
    /// </summary>
    public static List<ThemeRoundConfig> GenerateRounds(DateTime from, DateTime to, int minutes)
    {
        var rounds = new List<ThemeRoundConfig>();
        if (minutes <= 0) return rounds;

        var length = TimeSpan.FromMinutes(minutes);
        for (var start = from; start + length <= to && rounds.Count < MaxRounds; start += length)
        {
            var number = rounds.Count + 1;
            rounds.Add(new ThemeRoundConfig { Number = number, Start = start, End = start + length, Themes = new List<string> { number.ToString() } });
        }

        return rounds;
    }

    /// <summary>Why these rounds cannot be used together; null when they can.</summary>
    public static string? CheckRounds(IReadOnlyList<ThemeRoundConfig> rounds, int defaultMinutes)
    {
        if (rounds.Count > MaxRounds) return $"Er kunnen hoogstens {MaxRounds} rondes zijn.";

        foreach (var round in rounds)
        {
            if (CheckRound(round, defaultMinutes) is { } error) return error;
        }

        if (rounds.GroupBy(r => r.Number).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return $"Rondenummer {twice.Key} komt twee keer voor.";
        }

        var ordered = rounds.Select(r => ToRound(r, defaultMinutes)).OrderBy(r => r.Start).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Start < ordered[i - 1].End)
            {
                return $"Ronde {ordered[i - 1].Number} en ronde {ordered[i].Number} overlappen.";
            }
        }

        var theme = ordered.SelectMany(r => r.Themes.Select(t => (Theme: t, Round: r.Number)))
            .GroupBy(t => t.Theme)
            .FirstOrDefault(g => g.Count() > 1);
        if (theme != null)
        {
            return $"Thema {theme.Key} staat in meer dan één ronde ({string.Join(", ", theme.Select(t => t.Round))}).";
        }

        return null;
    }

    private static string? CheckRound(ThemeRoundConfig round, int defaultMinutes)
    {
        if (round.Number <= 0) return "Een rondenummer is 1 of hoger.";
        if (!IsValidEventTime(round.Start) || !IsValidEventTime(round.End)) return $"Ronde {round.Number} heeft een tijd die niet kan.";
        var end = round.End ?? round.Start.AddMinutes(defaultMinutes);
        if (end <= round.Start) return $"Ronde {round.Number} moet eindigen na de start.";
        var themes = round.Themes ?? new List<string>();
        if (themes.Count == 0) return $"Ronde {round.Number} heeft geen thema.";
        if (themes.Count > MaxThemesPerRound) return $"Ronde {round.Number} heeft meer dan {MaxThemesPerRound} thema's.";
        if (themes.Any(t => AssignmentCode.Normalize(t) is not { } code || AssignmentCode.IsLetter(code)))
        {
            return $"Ronde {round.Number}: een themanummer is een getal van 1 of hoger.";
        }

        if (round.Message?.Length > MaxSlackTextLength) return $"De tekst van ronde {round.Number} is te lang.";
        return null;
    }

    private static ThemeRound ToRound(ThemeRoundConfig round, int defaultMinutes) => new(
        round.Number,
        GameTime.FromGameTime(round.Start),
        GameTime.FromGameTime(round.End ?? round.Start.AddMinutes(defaultMinutes)),
        (round.Themes ?? new List<string>()).Select(AssignmentCode.Normalize).OfType<string>().Distinct().ToList(),
        string.IsNullOrWhiteSpace(round.Message) ? null : round.Message.Trim());

    private static ThemeRoundConfig CopyRound(ThemeRoundConfig r) => new()
    {
        Number = r.Number,
        Start = DateTime.SpecifyKind(r.Start, DateTimeKind.Unspecified),
        End = r.End is { } end ? DateTime.SpecifyKind(end, DateTimeKind.Unspecified) : null,
        Themes = (r.Themes ?? new List<string>()).Select(t => AssignmentCode.Normalize(t) ?? t.Trim()).ToList(),
        Message = string.IsNullOrWhiteSpace(r.Message) ? null : r.Message.Trim()
    };

    /// <summary>When the start information goes to the Avonturiers, in the game's time zone: set in the app, otherwise from appsettings.</summary>
    public DateTime? AdventurersStartAtGameTime
    {
        get { lock (_lock) return _adventurersStartAt != null ? _adventurersStartAt.Value : AdventurersOptions.StartAnnouncement?.PostAt; }
    }

    /// <summary>When the final location goes to the Avonturiers, in the game's time zone: set in the app, otherwise from appsettings.</summary>
    public DateTime? AdventurersFinalAtGameTime
    {
        get { lock (_lock) return _adventurersFinalAt != null ? _adventurersFinalAt.Value : AdventurersOptions.FinalAnnouncement?.PostAt; }
    }

    public DateTimeOffset? AdventurersStartAt => AdventurersStartAtGameTime is { } at && IsValidEventTime(at) ? GameTime.FromGameTime(at) : null;

    public DateTimeOffset? AdventurersFinalAt => AdventurersFinalAtGameTime is { } at && IsValidEventTime(at) ? GameTime.FromGameTime(at) : null;

    /// <summary>Sets when the start (or with <paramref name="final"/> the final) message is posted; null means only by hand.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetAdventurersPostTime(bool final, DateTime? at)
    {
        at = at is { } v ? ToWholeMinute(v) : null;
        if (!IsValidEventTime(at)) return $"Kies een tijd tussen {MinEventTime.Year} en {MaxEventTime.Year}.";

        lock (_lock)
        {
            if (final) _adventurersFinalAt = new TimeOverride { Value = at };
            else _adventurersStartAt = new TimeOverride { Value = at };
            Save();
        }

        _logger.LogWarning("Avonturiers {Kind} message time set to {Time} in the app", final ? "final" : "start", at?.ToString("yyyy-MM-dd HH:mm") ?? "none");
        Changed?.Invoke();
        return null;
    }

    /// <summary>When a broadcast (e.g. "start", "round-3-open") was posted in a channel; null when it was not.</summary>
    public DateTimeOffset? AdventurersPostedAt(string message, string channelId)
    {
        lock (_lock) return _adventurersPosted.TryGetValue(message, out var channels) && channels.TryGetValue(channelId, out var at) ? at : null;
    }

    /// <summary>Remembers that a broadcast was posted in a channel, so a restart does not post it again.</summary>
    public void MarkAdventurersPosted(string message, string channelId, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (!_adventurersPosted.TryGetValue(message, out var channels))
            {
                channels = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                _adventurersPosted[message] = channels;
            }

            channels[channelId] = at;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The unlock count the group's Toeristen channel was last told; null when it was never told.</summary>
    public int? UnlockNotified(Guid groupId)
    {
        lock (_lock) return _unlockNotified.TryGetValue(groupId, out var count) ? count : null;
    }

    public void MarkUnlockNotified(Guid groupId, int count)
    {
        lock (_lock)
        {
            if (_unlockNotified.TryGetValue(groupId, out var current) && current == count) return;
            _unlockNotified[groupId] = count;
            Save();
        }
    }

    private void LoadAdventurers(SavedAdventurers? saved)
    {
        _rounds = saved?.Rounds?.Where(r => r != null).Select(CopyRound).ToList();
        if (_rounds != null && CheckRounds(_rounds, RoundMinutes) is { } error)
        {
            // Rounds that cannot be used together would stop the round messages; appsettings applies until fixed
            _logger.LogError("Saved theme rounds are not usable ({Error}); the rounds from appsettings are used", error);
            _rounds = null;
        }

        _unlockPool = saved?.UnlockPool?.Select(AssignmentCode.Normalize).OfType<string>().Distinct().ToList();
        _requireUnlockedLocation = saved?.RequireUnlockedLocation;
        _adventurersStartAt = IsValidEventTime(saved?.StartAt?.Value) ? saved?.StartAt : null;
        _adventurersFinalAt = IsValidEventTime(saved?.FinalAt?.Value) ? saved?.FinalAt : null;
        _adventurersPosted = new Dictionary<string, Dictionary<string, DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (message, channels) in saved?.Posted ?? new Dictionary<string, Dictionary<string, DateTimeOffset>>())
        {
            if (channels != null) _adventurersPosted[message] = new Dictionary<string, DateTimeOffset>(channels, StringComparer.OrdinalIgnoreCase);
        }

        _unlockNotified = new Dictionary<Guid, int>(saved?.UnlockNotified ?? new Dictionary<Guid, int>());
    }

    private SavedAdventurers SaveAdventurers() => new()
    {
        Rounds = _rounds,
        UnlockPool = _unlockPool,
        RequireUnlockedLocation = _requireUnlockedLocation,
        StartAt = _adventurersStartAt,
        FinalAt = _adventurersFinalAt,
        Posted = _adventurersPosted,
        UnlockNotified = _unlockNotified
    };

    private sealed class SavedAdventurers
    {
        public List<ThemeRoundConfig>? Rounds { get; set; }
        public List<string>? UnlockPool { get; set; }
        public bool? RequireUnlockedLocation { get; set; }
        public TimeOverride? StartAt { get; set; }
        public TimeOverride? FinalAt { get; set; }
        public Dictionary<string, Dictionary<string, DateTimeOffset>>? Posted { get; set; }
        public Dictionary<Guid, int>? UnlockNotified { get; set; }
    }
}

/// <summary>One theme round as points in time. <see cref="End"/> is not part of the round.</summary>
public sealed record ThemeRound(int Number, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> Themes, string? Message)
{
    public bool Contains(DateTimeOffset at) => Start <= at && at < End;

    public bool IsClosed(DateTimeOffset now) => now >= End;

    public bool IsRunning(DateTimeOffset now) => Contains(now);

    /// <summary>"thema 3" or "thema's 3 en 4".</summary>
    public string ThemesLabel => Themes.Count == 1
        ? $"thema {Themes[0]}"
        : $"thema's {string.Join(", ", Themes.Take(Themes.Count - 1))} en {Themes[^1]}";
}
