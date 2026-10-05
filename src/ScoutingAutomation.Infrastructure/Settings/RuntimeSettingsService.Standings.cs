using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>
/// Whether groups see the live standing. The jury can hide it by hand or for set periods, optionally also on the
/// beamer scoreboard, and publish an interim standing that groups see while it is hidden. Saved with the other settings.
/// </summary>
public partial class RuntimeSettingsService
{
    /// <summary>More periods than this is a typing loop, not a game.</summary>
    public const int MaxHideWindows = 50;

    private bool _standingHiddenManually;
    private List<StandingHideWindow> _hideWindows = new();
    private bool _hideStandingOnBeamer;
    private InterimStanding? _interimStanding;

    /// <summary>Hidden by the switch, regardless of the periods.</summary>
    public bool StandingHiddenManually
    {
        get { lock (_lock) return _standingHiddenManually; }
    }

    /// <summary>The periods in which the standing is hidden, earliest first.</summary>
    public IReadOnlyList<StandingHideWindow> StandingHideWindows
    {
        get { lock (_lock) return _hideWindows.OrderBy(w => w.From).ToList(); }
    }

    /// <summary>Whether the beamer scoreboard hides the standing too while it is hidden for groups.</summary>
    public bool HideStandingOnBeamer
    {
        get { lock (_lock) return _hideStandingOnBeamer; }
    }

    /// <summary>The interim standing groups see while the standing is hidden; null when none was published.</summary>
    public InterimStanding? InterimStanding
    {
        get { lock (_lock) return _interimStanding; }
    }

    /// <summary>Whether groups do not see the live standing at <paramref name="now"/>.</summary>
    public bool IsStandingHidden(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _standingHiddenManually || _hideWindows.Any(w => GameTime.FromGameTime(w.From) <= now && now < GameTime.FromGameTime(w.To));
        }
    }

    /// <summary>The next moment a period starts or ends after <paramref name="now"/>; null when none is coming.</summary>
    public DateTimeOffset? NextStandingChange(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _hideWindows
                .SelectMany(w => new[] { GameTime.FromGameTime(w.From), GameTime.FromGameTime(w.To) })
                .Where(at => at > now)
                .Select(at => (DateTimeOffset?)at)
                .Min();
        }
    }

    public void SetStandingHidden(bool hidden)
    {
        lock (_lock)
        {
            if (_standingHiddenManually == hidden) return;
            _standingHiddenManually = hidden;
            Save();
        }

        _logger.LogWarning("Standing {State} for groups by hand", hidden ? "hidden" : "shown");
        Changed?.Invoke();
    }

    /// <summary>Adds a period, in the game's time zone, in which groups do not see the live standing.</summary>
    /// <returns>Null when saved, otherwise why not.</returns>
    public string? AddStandingHideWindow(DateTime from, DateTime to)
    {
        from = ToWholeMinute(from);
        to = ToWholeMinute(to);
        if (!IsValidEventTime(from) || !IsValidEventTime(to))
        {
            return $"Kies tijden tussen {MinEventTime.Year} en {MaxEventTime.Year}.";
        }

        if (to <= from)
        {
            return "Het einde moet na het begin liggen.";
        }

        var window = new StandingHideWindow(Guid.NewGuid(), from, to);
        lock (_lock)
        {
            if (_hideWindows.Count >= MaxHideWindows)
            {
                return $"Er kunnen hoogstens {MaxHideWindows} periodes zijn.";
            }

            _hideWindows.Add(window);
            Save();
        }

        _logger.LogWarning("Standing hidden for groups from {From} to {To}", from.ToString("yyyy-MM-dd HH:mm"), to.ToString("yyyy-MM-dd HH:mm"));
        Changed?.Invoke();
        return null;
    }

    public bool RemoveStandingHideWindow(Guid id)
    {
        lock (_lock)
        {
            if (_hideWindows.RemoveAll(w => w.Id == id) == 0) return false;
            Save();
        }

        _logger.LogWarning("Hidden standing period removed");
        Changed?.Invoke();
        return true;
    }

    public void SetHideStandingOnBeamer(bool hide)
    {
        lock (_lock)
        {
            if (_hideStandingOnBeamer == hide) return;
            _hideStandingOnBeamer = hide;
            Save();
        }

        _logger.LogWarning("Hidden standing on the beamer switched {State}", hide ? "on" : "off");
        Changed?.Invoke();
    }

    /// <summary>Publishes the standing as it is now, for groups to see while the live standing is hidden.</summary>
    public void PublishInterimStanding(IEnumerable<InterimStandingRow> rows, DateTimeOffset at)
    {
        var standing = new InterimStanding(at, rows.OrderBy(r => r.Rank).ThenBy(r => r.GroupName).ToList());
        lock (_lock)
        {
            _interimStanding = standing;
            Save();
        }

        _logger.LogWarning("Interim standing published with {Count} groups", standing.Rows.Count);
        Changed?.Invoke();
    }

    public void ClearInterimStanding()
    {
        lock (_lock)
        {
            if (_interimStanding == null) return;
            _interimStanding = null;
            Save();
        }

        _logger.LogWarning("Interim standing removed");
        Changed?.Invoke();
    }

    private static DateTime ToWholeMinute(DateTime value) =>
        DateTime.SpecifyKind(value.AddTicks(-(value.Ticks % TimeSpan.TicksPerMinute)), DateTimeKind.Unspecified);

    private void LoadStandings(SavedStandings? saved)
    {
        _standingHiddenManually = saved?.HiddenManually ?? false;
        _hideStandingOnBeamer = saved?.HideOnBeamer ?? false;
        // A period that cannot be used is dropped, like an unusable event time
        _hideWindows = (saved?.HideWindows ?? new List<StandingHideWindow>())
            .Where(w => w != null && IsValidEventTime(w.From) && IsValidEventTime(w.To) && w.To > w.From)
            .Take(MaxHideWindows)
            .ToList();
        _interimStanding = saved?.InterimStanding is { Rows: not null } interim ? interim : null;
    }

    private void ResetStandings() => LoadStandings(null);

    private SavedStandings SaveStandings() => new()
    {
        HiddenManually = _standingHiddenManually,
        HideWindows = _hideWindows,
        HideOnBeamer = _hideStandingOnBeamer,
        InterimStanding = _interimStanding
    };

    private sealed class SavedStandings
    {
        public bool HiddenManually { get; set; }
        public List<StandingHideWindow>? HideWindows { get; set; }
        public bool HideOnBeamer { get; set; }
        public InterimStanding? InterimStanding { get; set; }
    }
}

/// <summary>A period in which groups do not see the live standing, in the game's time zone; <see cref="To"/> is not included.</summary>
public sealed record StandingHideWindow(Guid Id, DateTime From, DateTime To);

/// <summary>The standing at <see cref="PublishedAt"/>, as published by the jury.</summary>
public sealed record InterimStanding(DateTimeOffset PublishedAt, IReadOnlyList<InterimStandingRow> Rows);

public sealed record InterimStandingRow(Guid GroupId, string GroupName, int Rank, int Score);
