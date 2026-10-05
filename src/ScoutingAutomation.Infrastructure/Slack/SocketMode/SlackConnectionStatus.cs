using ScoutingAutomation.Infrastructure.Slack.Events;

namespace ScoutingAutomation.Infrastructure.Slack.SocketMode;

public enum SlackConnectionState
{
    /// <summary>Socket Mode is not used (simulation, or events arrive over HTTP).</summary>
    Disabled,
    Connecting,
    Connected,
    Error
}

public static class SlackConnectionStateExtensions
{
    /// <summary>The Dutch name shown in the app.</summary>
    public static string Label(this SlackConnectionState state) => state switch
    {
        SlackConnectionState.Connecting => "verbinden",
        SlackConnectionState.Connected => "verbonden",
        SlackConnectionState.Error => "fout",
        _ => "uit"
    };
}

/// <summary>
/// Current state of the Socket Mode connection, for display in the app.
/// </summary>
public class SlackConnectionStatus
{
    private readonly object _lock = new();
    private readonly SlackEventLog? _eventLog;

    /// <param name="eventLog">Gets an entry when the connection comes up or fails, for the events page.</param>
    public SlackConnectionStatus(SlackEventLog? eventLog = null)
    {
        _eventLog = eventLog;
    }

    public SlackConnectionState State { get; private set; } = SlackConnectionState.Disabled;
    public string? Detail { get; private set; }
    public DateTimeOffset? Since { get; private set; }

    public void Set(SlackConnectionState state, string? detail = null)
    {
        bool changed;
        lock (_lock)
        {
            changed = State != state;
            if (changed)
            {
                Since = DateTimeOffset.UtcNow;
            }

            State = state;
            Detail = detail;
        }

        // Every failure shows, also a repeated one: each is another attempt that did not work
        if (state == SlackConnectionState.Error)
        {
            _eventLog?.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Failed,
                $"Verbinding met Slack mislukt of verbroken: {detail ?? "onbekende fout"}", "Slack Socket Mode");
        }
        else if (changed && state == SlackConnectionState.Connected)
        {
            _eventLog?.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Processed, "Verbonden met Slack", "Slack Socket Mode");
        }
    }

    /// <summary>The connection ended normally, e.g. Slack refreshes it every few hours; the app connects again.</summary>
    public void Closed(string reason)
    {
        Set(SlackConnectionState.Connecting, reason);
        _eventLog?.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Ignored, $"{reason}; opnieuw verbinden", "Slack Socket Mode");
    }
}
