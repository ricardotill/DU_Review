using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Web.Standings;

/// <summary>
/// Tells a page whether the standing is hidden for groups, and calls it back when that changes:
/// when the jury changes a setting, and at the start and end of each hidden period.
/// </summary>
public sealed class StandingClock : IDisposable
{
    private readonly RuntimeSettingsService _runtime;
    private readonly TimeProvider _time;
    private readonly Func<Task> _changed;
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="changed">Called from any thread; a component passes something that runs through InvokeAsync.</param>
    public StandingClock(RuntimeSettingsService runtime, TimeProvider time, Func<Task> changed)
    {
        _runtime = runtime;
        _time = time;
        _changed = changed;
        _runtime.Changed += OnRuntimeChanged;
        Schedule();
    }

    /// <summary>Whether groups do not see the live standing now.</summary>
    public bool IsHidden => _runtime.IsStandingHidden(_time.GetUtcNow());

    /// <summary>Whether the beamer scoreboard hides the standing now.</summary>
    public bool IsHiddenOnBeamer => _runtime.HideStandingOnBeamer && IsHidden;

    public InterimStanding? Interim => _runtime.InterimStanding;

    private void OnRuntimeChanged()
    {
        Schedule();
        _ = NotifyAsync();
    }

    // One timer for the next start or end of a period; set again when it fires or the settings change
    private void Schedule()
    {
        lock (this)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed) return;

            var now = _time.GetUtcNow();
            if (_runtime.NextStandingChange(now) is { } at)
            {
                // Just after the moment, so IsHidden sees it passed
                _timer = _time.CreateTimer(_ =>
                {
                    Schedule();
                    _ = NotifyAsync();
                }, null, at - now + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task NotifyAsync()
    {
        if (_disposed) return;
        try
        {
            await _changed();
        }
        catch (ObjectDisposedException)
        {
            // Page closed meanwhile
        }
    }

    public void Dispose()
    {
        _runtime.Changed -= OnRuntimeChanged;
        lock (this)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
        }
    }
}

public static class InterimStandingExtensions
{
    /// <summary>The interim standing in the shape the scoreboard components take.</summary>
    public static SubmissionStatistics ToStatistics(this InterimStanding standing) => new()
    {
        GroupStatistics = standing.Rows
            .Select(r => new GroupStatistic { GroupId = r.GroupId, GroupName = r.GroupName, Rank = r.Rank, Score = r.Score })
            .ToList()
    };

    /// <summary>The rows to publish from the live statistics.</summary>
    public static IEnumerable<InterimStandingRow> ToInterimRows(this SubmissionStatistics stats) =>
        stats.GroupStatistics.Select(g => new InterimStandingRow(g.GroupId, g.GroupName, g.Rank, g.Score));
}
