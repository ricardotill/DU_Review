using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Web.Components.Shared;

/// <summary>
/// Tells a page whether a part of the game has started, and calls it back when that changes: at the start time,
/// and when the jury changes a setting.
/// </summary>
public sealed class StartClock : IDisposable
{
    private readonly RuntimeSettingsService _runtime;
    private readonly TimeProvider _time;
    private readonly Func<DateTimeOffset?> _start;
    private readonly Func<Task> _changed;
    private ITimer? _timer;
    private bool _disposed;

    /// <param name="start">The start to watch, read again on every change; null means there is no start: started.</param>
    /// <param name="changed">Called from any thread; a component passes something that runs through InvokeAsync.</param>
    public StartClock(RuntimeSettingsService runtime, TimeProvider time, Func<DateTimeOffset?> start, Func<Task> changed)
    {
        _runtime = runtime;
        _time = time;
        _start = start;
        _changed = changed;
        _runtime.Changed += OnRuntimeChanged;
        Schedule();
    }

    /// <summary>The start, while it is still to come; null once started or without a start.</summary>
    public DateTimeOffset? Upcoming => _start() is { } start && _time.GetUtcNow() < start ? start : null;

    public bool HasStarted => Upcoming == null;

    private void OnRuntimeChanged()
    {
        Schedule();
        _ = NotifyAsync();
    }

    // One timer for the start; set again when the settings change
    private void Schedule()
    {
        lock (this)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || Upcoming is not { } at) return;

            // Just after the moment, so HasStarted sees it passed
            _timer = _time.CreateTimer(_ => _ = NotifyAsync(), null, at - _time.GetUtcNow() + TimeSpan.FromMilliseconds(50), Timeout.InfiniteTimeSpan);
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
