using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Services;

public enum SlackEventKind
{
    Message,
    Edited,
    Deleted,
    ReactionAdded,
    ReactionRemoved
}

public enum SlackEventOutcome
{
    Processed,
    Ignored,
    Failed
}

/// <param name="Channel">The message's channel, or the channel of the message reacted to.</param>
/// <param name="Detail">What the app did, or why it did nothing.</param>
public sealed record SlackEventLogEntry(
    Guid Id,
    DateTimeOffset ReceivedAt,
    TimeSpan Duration,
    SlackEventKind Kind,
    string? Channel,
    string? User,
    string? Text,
    string? Reaction,
    int FileCount,
    SlackEventOutcome Outcome,
    string Detail,
    IReadOnlyList<Guid> SubmissionIds);

/// <summary>
/// The most recent Slack events and their outcome, for the live events page.
/// Kept in memory only: the history starts again when the app restarts.
/// </summary>
public class SlackEventLog
{
    public const int Capacity = 500;

    private readonly LinkedList<SlackEventLogEntry> _entries = new();
    private readonly object _lock = new();
    private readonly ILogger<SlackEventLog> _logger;

    public SlackEventLog(ILogger<SlackEventLog> logger)
    {
        _logger = logger;
    }

    public event Action<SlackEventLogEntry>? Added;

    public void Add(SlackEventLogEntry entry)
    {
        lock (_lock)
        {
            _entries.AddFirst(entry);
            if (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        var handlers = Added?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (var handler in handlers.Cast<Action<SlackEventLogEntry>>())
        {
            // One failing page must not stop the others or the event processing
            try
            {
                handler(entry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Slack event log handler failed");
            }
        }
    }

    /// <summary>The kept events, newest first.</summary>
    public IReadOnlyList<SlackEventLogEntry> Recent()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }
}
