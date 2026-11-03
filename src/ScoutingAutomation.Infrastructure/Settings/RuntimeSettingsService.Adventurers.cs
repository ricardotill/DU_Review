using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>
/// The Avonturiers settings that can change while the app runs: the theme rounds, the locations open at the start,
/// the times of the start and final messages, which channels got which message, and the open-assignment count each
/// Toeristen channel was last told. Whether the part is on at all only comes from appsettings.
/// </summary>
public partial class RuntimeSettingsService
{
    /// <summary>The highest "locations 1 through N" that can be set; the same as the highest location number.</summary>
    public const int MaxOpenLocations = 9999;

    /// <summary>More rounds than this is a typing loop, not a game.</summary>
    public const int MaxRounds = 60;

    /// <summary>Most super locations; more is a typing loop or a broken file, not a game.</summary>
    public const int MaxSuperLocations = 1000;

    /// <summary>Most theme numbers in one round.</summary>
    public const int MaxThemesPerRound = 10;

    private List<ThemeRoundConfig>? _rounds;
    private int? _initialOpenLocations;
    private SuperLocationsConfig? _superLocations;
    // The numbers that count double now; built on first read and dropped on every change
    private IReadOnlySet<int>? _superLocationSet;
    private int? _themeMaxPoints;
    private int? _finalMaxPoints;
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
            SlackText.RoundHandover => settings.RoundHandoverMessage,
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

    /// <summary>The Toeristen assignments that are open from the start (numbers, lowest first).</summary>
    public int InitialOpenAssignments => Math.Max(0, AdventurersOptions.InitialOpenAssignments);

    /// <summary>
    /// How many locations (1 through this number) are open for a group that has no number of its own:
    /// set in the app, otherwise from appsettings.
    /// </summary>
    public int InitialOpenLocations
    {
        get { lock (_lock) return Math.Max(0, _initialOpenLocations ?? AdventurersOptions.InitialOpenLocations); }
    }

    /// <summary>Whether the number of locations open at the start was changed in the app.</summary>
    public bool IsInitialOpenLocationsChanged
    {
        get { lock (_lock) return _initialOpenLocations != null; }
    }

    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetInitialOpenLocations(int count)
    {
        if (count is < 0 or > MaxOpenLocations) return $"Kies een aantal tussen 0 en {MaxOpenLocations}.";

        lock (_lock)
        {
            if (_initialOpenLocations == count) return null;
            _initialOpenLocations = count;
            Save();
        }

        _logger.LogWarning("Locations open at the start set to 1-{Count} in the app", count);
        Changed?.Invoke();
        return null;
    }

    public void ResetInitialOpenLocations()
    {
        lock (_lock)
        {
            if (_initialOpenLocations == null) return;
            _initialOpenLocations = null;
            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The super locations as set: in the app, otherwise from appsettings. Also when off.</summary>
    public SuperLocationsConfig SuperLocationsSetting
    {
        get
        {
            lock (_lock)
            {
                var source = _superLocations ?? AdventurersOptions.SuperLocations ?? new SuperLocationsConfig();
                return new SuperLocationsConfig { Enabled = source.Enabled, Numbers = CleanNumbers(source.Numbers) };
            }
        }
    }

    /// <summary>The location numbers that count double now; empty when the super locations are off.</summary>
    public IReadOnlyCollection<int> SuperLocations
    {
        get
        {
            lock (_lock)
            {
                if (_superLocationSet == null)
                {
                    var source = _superLocations ?? AdventurersOptions.SuperLocations ?? new SuperLocationsConfig();
                    _superLocationSet = source.Enabled ? CleanNumbers(source.Numbers).ToHashSet() : new HashSet<int>();
                }

                return _superLocationSet;
            }
        }
    }

    /// <summary>Whether the super locations were changed in the app.</summary>
    public bool IsSuperLocationsChanged
    {
        get { lock (_lock) return _superLocations != null; }
    }

    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetSuperLocations(bool enabled, IEnumerable<int> numbers)
    {
        var list = numbers.Distinct().ToList();
        if (list.Any(n => n is < 1 or > MaxOpenLocations)) return $"Kies locatienummers tussen 1 en {MaxOpenLocations}.";
        if (list.Count > MaxSuperLocations) return $"Kies hoogstens {MaxSuperLocations} supercoördinaten.";

        var setting = new SuperLocationsConfig { Enabled = enabled, Numbers = CleanNumbers(list) };
        lock (_lock)
        {
            if (_superLocations is { } current && current.Enabled == setting.Enabled && current.Numbers.SequenceEqual(setting.Numbers)) return null;
            _superLocations = setting;
            _superLocationSet = null;
            Save();
        }

        _logger.LogWarning("Super locations set in the app: {State}, {Numbers}", enabled ? "on" : "off", string.Join(", ", setting.Numbers));
        Changed?.Invoke();
        return null;
    }

    public void ResetSuperLocations()
    {
        lock (_lock)
        {
            if (_superLocations == null) return;
            _superLocations = null;
            _superLocationSet = null;
            Save();
        }

        Changed?.Invoke();
    }

    private static List<int> CleanNumbers(IEnumerable<int>? numbers) =>
        (numbers ?? Enumerable.Empty<int>()).Where(n => n is >= 1 and <= MaxOpenLocations).Distinct().Order().Take(MaxSuperLocations).ToList();

    /// <summary>The most points the half-hour assignments (theme rounds) give a group together: set in the app, otherwise from appsettings.</summary>
    public int ThemeMaxPoints
    {
        get { lock (_lock) return Math.Max(0, _themeMaxPoints ?? AdventurersOptions.ThemeMaxPoints); }
    }

    /// <summary>The most points of the hand-entered final game that count: set in the app, otherwise from appsettings.</summary>
    public int FinalMaxPoints
    {
        get { lock (_lock) return Math.Max(0, _finalMaxPoints ?? AdventurersOptions.FinalMaxPoints); }
    }

    /// <summary>Whether either maximum was changed in the app.</summary>
    public bool IsMaxPointsChanged
    {
        get { lock (_lock) return _themeMaxPoints != null || _finalMaxPoints != null; }
    }

    /// <summary>Sets both maximums. Points above them are kept, but count only up to the maximum.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetMaxPoints(int themeMax, int finalMax)
    {
        if (themeMax is < 0 or > BonusPointsChange.MaxPoints || finalMax is < 0 or > BonusPointsChange.MaxPoints)
        {
            return $"Kies een maximum van 0 tot en met {BonusPointsChange.MaxPoints} punten.";
        }

        lock (_lock)
        {
            if (_themeMaxPoints == themeMax && _finalMaxPoints == finalMax) return null;
            _themeMaxPoints = themeMax;
            _finalMaxPoints = finalMax;
            Save();
        }

        _logger.LogWarning("Avonturiers maximum set in the app: {Theme} for the half-hour assignments, {Final} for the final game", themeMax, finalMax);
        Changed?.Invoke();
        return null;
    }

    /// <summary>Goes back to the maximums from appsettings.</summary>
    public void ResetMaxPoints()
    {
        lock (_lock)
        {
            if (_themeMaxPoints == null && _finalMaxPoints == null) return;
            _themeMaxPoints = null;
            _finalMaxPoints = null;
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
            return $"Half-uur opdracht {theme.Key} staat in meer dan één ronde ({string.Join(", ", theme.Select(t => t.Round))}).";
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
        if (themes.Count == 0) return $"Ronde {round.Number} heeft geen half-uur opdracht.";
        if (themes.Count > MaxThemesPerRound) return $"Ronde {round.Number} heeft meer dan {MaxThemesPerRound} half-uur opdrachten.";
        if (themes.Any(t => AssignmentCode.Normalize(t) is not { } code || AssignmentCode.IsLetter(code)))
        {
            return $"Ronde {round.Number}: het nummer van een half-uur opdracht is een getal van 1 of hoger.";
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

    /// <summary>
    /// The start of the Avonturiers part, in the game's time zone; the start information goes out then. Set in the app,
    /// otherwise from appsettings, otherwise the start of the event.
    /// </summary>
    public DateTime? AdventurersStartAtGameTime => AdventurersOwnTime(final: false) ?? EventStartGameTime;

    /// <summary>
    /// The end of the Avonturiers part, in the game's time zone; the final location goes out then. Set in the app,
    /// otherwise from appsettings, otherwise the end of the event.
    /// </summary>
    public DateTime? AdventurersFinalAtGameTime => AdventurersOwnTime(final: true) ?? EventEndGameTime;

    /// <summary>The Avonturiers' own start (or with <paramref name="final"/> end) time, without the event's as fallback.</summary>
    public DateTime? AdventurersOwnTime(bool final)
    {
        lock (_lock)
        {
            var changed = final ? _adventurersFinalAt : _adventurersStartAt;
            if (changed?.Value != null) return changed.Value;
        }

        var configured = (final ? AdventurersOptions.FinalAnnouncement : AdventurersOptions.StartAnnouncement)?.PostAt;
        return IsValidEventTime(configured) ? configured : null;
    }

    public DateTimeOffset? AdventurersStartAt => AdventurersStartAtGameTime is { } at && IsValidEventTime(at) ? GameTime.FromGameTime(at) : null;

    public DateTimeOffset? AdventurersFinalAt => AdventurersFinalAtGameTime is { } at && IsValidEventTime(at) ? GameTime.FromGameTime(at) : null;

    /// <summary>Sets the start (or with <paramref name="final"/> the end) of the Avonturiers part; null means the event's.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? SetAdventurersPostTime(bool final, DateTime? at)
    {
        at = at is { } v ? ToWholeMinute(v) : null;
        if (!IsValidEventTime(at)) return $"Kies een tijd tussen {MinEventTime.Year} en {MaxEventTime.Year}.";

        lock (_lock)
        {
            // Without a time of its own the part follows the event, so the check uses what would apply
            var start = final ? AdventurersStartAtGameTime : at ?? EventStartGameTime;
            var end = final ? at ?? EventEndGameTime : AdventurersFinalAtGameTime;
            if (start is { } s && end is { } e && e <= s) return "Het einde van de Avonturiers moet na de start liggen.";

            if (final) _adventurersFinalAt = new TimeOverride { Value = at };
            else _adventurersStartAt = new TimeOverride { Value = at };
            Save();
        }

        _logger.LogWarning("Avonturiers {Kind} message time set to {Time} in the app", final ? "final" : "start", at?.ToString("yyyy-MM-dd HH:mm") ?? "none");
        Changed?.Invoke();
        return null;
    }

    /// <summary>Whether the time of the start (or with <paramref name="final"/> the final) message was set in the app.</summary>
    public bool IsAdventurersPostTimeChanged(bool final)
    {
        lock (_lock) return (final ? _adventurersFinalAt : _adventurersStartAt) != null;
    }

    /// <summary>Goes back to the time of the start (or with <paramref name="final"/> the final) message from appsettings.</summary>
    public void ResetAdventurersPostTime(bool final)
    {
        lock (_lock)
        {
            if ((final ? _adventurersFinalAt : _adventurersStartAt) == null) return;
            if (final) _adventurersFinalAt = null;
            else _adventurersStartAt = null;
            Save();
        }

        _logger.LogWarning("Avonturiers {Kind} message time reset to appsettings", final ? "final" : "start");
        Changed?.Invoke();
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

    /// <summary>The number of open assignments the group's Toeristen were last told; null when they were never told.</summary>
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

    /// <summary>
    /// Forgets what every group was told. Used when the first locations arrive: a count from before, when everything
    /// was open, would otherwise look like a drop and post a correction to every group.
    /// </summary>
    public void ResetUnlockNotified()
    {
        lock (_lock)
        {
            if (_unlockNotified.Count == 0) return;
            _unlockNotified = new Dictionary<Guid, int>();
            Save();
        }

        _logger.LogInformation("Forgot the open-assignment counts told to the groups: the first locations were added");
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

        _initialOpenLocations = saved?.InitialOpenLocations is >= 0 and <= MaxOpenLocations ? saved.InitialOpenLocations : null;
        _superLocations = saved?.SuperLocations is { } super ? new SuperLocationsConfig { Enabled = super.Enabled, Numbers = CleanNumbers(super.Numbers) } : null;
        _superLocationSet = null;
        if (saved?.SuperLocations?.Numbers is { } savedNumbers && savedNumbers.Distinct().Count() != _superLocations!.Numbers.Count)
        {
            _logger.LogWarning("Saved super locations had {Dropped} unusable numbers; they are ignored", savedNumbers.Distinct().Count() - _superLocations.Numbers.Count);
        }

        _themeMaxPoints = saved?.ThemeMaxPoints is >= 0 and <= BonusPointsChange.MaxPoints ? saved.ThemeMaxPoints : null;
        _finalMaxPoints = saved?.FinalMaxPoints is >= 0 and <= BonusPointsChange.MaxPoints ? saved.FinalMaxPoints : null;
        _adventurersStartAt = IsValidEventTime(saved?.StartAt?.Value) ? saved?.StartAt : null;
        _adventurersFinalAt = IsValidEventTime(saved?.FinalAt?.Value) ? saved?.FinalAt : null;
        _adventurersPosted = new Dictionary<string, Dictionary<string, DateTimeOffset>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (message, channels) in saved?.Posted ?? new Dictionary<string, Dictionary<string, DateTimeOffset>>())
        {
            if (channels != null) _adventurersPosted[message] = new Dictionary<string, DateTimeOffset>(channels, StringComparer.OrdinalIgnoreCase);
        }

        _unlockNotified = new Dictionary<Guid, int>(saved?.AssignmentsNotified ?? new Dictionary<Guid, int>());
    }

    private SavedAdventurers SaveAdventurers() => new()
    {
        Rounds = _rounds,
        InitialOpenLocations = _initialOpenLocations,
        SuperLocations = _superLocations,
        ThemeMaxPoints = _themeMaxPoints,
        FinalMaxPoints = _finalMaxPoints,
        StartAt = _adventurersStartAt,
        FinalAt = _adventurersFinalAt,
        Posted = _adventurersPosted,
        AssignmentsNotified = _unlockNotified
    };

    private sealed class SavedAdventurers
    {
        public List<ThemeRoundConfig>? Rounds { get; set; }
        public int? InitialOpenLocations { get; set; }
        public SuperLocationsConfig? SuperLocations { get; set; }
        public int? ThemeMaxPoints { get; set; }
        public int? FinalMaxPoints { get; set; }
        public TimeOverride? StartAt { get; set; }
        public TimeOverride? FinalAt { get; set; }
        public Dictionary<string, Dictionary<string, DateTimeOffset>>? Posted { get; set; }
        // Open assignments per group; a new name, so a count of locations saved by an earlier version is not read as one
        public Dictionary<Guid, int>? AssignmentsNotified { get; set; }
    }
}

/// <summary>One theme round as points in time. <see cref="End"/> is not part of the round.</summary>
public sealed record ThemeRound(int Number, DateTimeOffset Start, DateTimeOffset End, IReadOnlyList<string> Themes, string? Message)
{
    public bool Contains(DateTimeOffset at) => Start <= at && at < End;

    public bool IsClosed(DateTimeOffset now) => now >= End;

    public bool IsRunning(DateTimeOffset now) => Contains(now);

    /// <summary>"half-uur opdracht 3" or "half-uur opdrachten 3 en 4".</summary>
    public string ThemesLabel => Themes.Count == 1
        ? $"half-uur opdracht {Themes[0]}"
        : $"half-uur opdrachten {string.Join(", ", Themes.Take(Themes.Count - 1))} en {Themes[^1]}";
}
