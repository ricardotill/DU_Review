namespace ScoutingAutomation.Domain.Entities;

public enum FailedSlackEventStatus
{
    Pending,
    Resolved,
    Dismissed
}

/// <summary>
/// A Slack event the app could not process. Kept so an organiser can run it again,
/// instead of rebuilding a whole group from Slack.
/// </summary>
public class FailedSlackEvent
{
    public Guid Id { get; set; }

    /// <summary>The same event delivered again (a Slack retry, a replay) updates this row instead of adding one.</summary>
    public string EventKey { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;
    public string? ChannelId { get; set; }
    public string? MessageTs { get; set; }

    /// <summary>The Slack event as JSON, to run it again exactly as it arrived.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public FailedSlackEventStatus Status { get; set; } = FailedSlackEventStatus.Pending;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
