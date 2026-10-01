namespace ScoutingAutomation.Infrastructure.Services;

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

    public SlackConnectionState State { get; private set; } = SlackConnectionState.Disabled;
    public string? Detail { get; private set; }
    public DateTimeOffset? Since { get; private set; }

    public void Set(SlackConnectionState state, string? detail = null)
    {
        lock (_lock)
        {
            if (State != state)
            {
                Since = DateTimeOffset.UtcNow;
            }

            State = state;
            Detail = detail;
        }
    }
}
