namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>Slack answered a Web API call with "ok": false.</summary>
public class SlackApiException : Exception
{
    public SlackApiException(string method, string error)
        : base($"Slack {method} failed: {error}")
    {
        Method = method;
        Error = error;
    }

    public string Method { get; }

    /// <summary>Slack's error code, such as "not_in_channel".</summary>
    public string Error { get; }
}
