using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Routes an Events API envelope to the workflow. Shared by the HTTP webhook and Socket Mode.
/// </summary>
public interface ISlackEventDispatcher
{
    /// <returns>The workflow result, or null when the event is not relevant.</returns>
    Task<object?> DispatchAsync(SlackEventEnvelopeDto envelope, CancellationToken ct = default);
}

public class SlackEventDispatcher : ISlackEventDispatcher
{
    private readonly ISubmissionWorkflowService _workflowService;
    private readonly SlackProcessingGate _gate;
    private readonly SlackChannelLocks _channels;
    private readonly SlackEventLog _eventLog;
    private readonly TimeProvider _time;
    private readonly ILogger<SlackEventDispatcher> _logger;
    private readonly IServiceScopeFactory? _scopes;

    public SlackEventDispatcher(
        ISubmissionWorkflowService workflowService,
        SlackProcessingGate gate,
        SlackEventLog eventLog,
        TimeProvider time,
        ILogger<SlackEventDispatcher> logger,
        SlackChannelLocks? channels = null,
        IServiceScopeFactory? scopes = null)
    {
        _scopes = scopes;
        _workflowService = workflowService;
        _gate = gate;
        _channels = channels ?? new SlackChannelLocks();
        _eventLog = eventLog;
        _time = time;
        _logger = logger;
    }

    public async Task<object?> DispatchAsync(SlackEventEnvelopeDto envelope, CancellationToken ct = default)
    {
        if (envelope.Type != "event_callback" || envelope.Event == null)
        {
            return null;
        }

        var ev = envelope.Event;

        // Waits while a rebuild from Slack history runs, so no event is processed twice or lost
        using var _ = await _gate.EnterAsync(ct);

        // The catch-up and a dashboard override change the same submissions: they wait for this event, or it for them
        using var __ = await _channels.EnterAsync(SlackEventQueue.LaneFor(envelope), ct);

        // Messages with uploads arrive with subtype "file_share"; a thread reply also sent to the channel is a "thread_broadcast"
        if (ev.Type == "message" && (string.IsNullOrEmpty(ev.Subtype) || ev.Subtype is "file_share" or "thread_broadcast"))
        {
            _logger.LogInformation("Processing Slack message event in channel {Channel}", ev.Channel);
            return await RecordAsync(ev, SlackEventKind.Message, () => _workflowService.ProcessSlackMessageAsync(ev, ct));
        }

        // A deleted message that has thread replies stays as a "tombstone": Slack sends that as an edit
        if (ev.Type == "message" && ev.Subtype == "message_changed" && ev.Message?.Subtype == "tombstone")
        {
            _logger.LogInformation("Processing deleted Slack message {Ts} with replies in channel {Channel}", ev.Message.Ts, ev.Channel);
            var deleted = new SlackEventItemDto
            {
                Type = "message",
                Subtype = "message_deleted",
                Channel = ev.Channel,
                DeletedTs = ev.Message.Ts,
                PreviousMessage = ev.PreviousMessage
            };
            return await RecordAsync(ev, SlackEventKind.Deleted, () => _workflowService.ProcessSlackMessageDeletedAsync(deleted, ct));
        }

        if (ev.Type == "message" && ev.Subtype == "message_changed")
        {
            _logger.LogInformation("Processing edited Slack message {Ts} in channel {Channel}", ev.Message?.Ts, ev.Channel);
            return await RecordAsync(ev, SlackEventKind.Edited, () => _workflowService.ProcessSlackMessageChangedAsync(ev, ct));
        }

        if (ev.Type == "message" && ev.Subtype == "message_deleted")
        {
            _logger.LogInformation("Processing deleted Slack message {Ts} in channel {Channel}", ev.DeletedTs, ev.Channel);
            return await RecordAsync(ev, SlackEventKind.Deleted, () => _workflowService.ProcessSlackMessageDeletedAsync(ev, ct));
        }

        if (ev.Type == "reaction_added")
        {
            _logger.LogInformation("Processing Slack reaction_added '{Reaction}' in channel {Channel}", ev.Reaction, ev.Item?.Channel);
            return await RecordAsync(ev, SlackEventKind.ReactionAdded, () => _workflowService.ProcessSlackReactionAsync(ev, ct));
        }

        if (ev.Type == "reaction_removed")
        {
            _logger.LogInformation("Processing Slack reaction_removed '{Reaction}' in channel {Channel}", ev.Reaction, ev.Item?.Channel);
            return await RecordAsync(ev, SlackEventKind.ReactionRemoved, () => _workflowService.ProcessSlackReactionRemovedAsync(ev, ct));
        }

        return null;
    }

    /// <summary>Runs the workflow and adds the event and its outcome to the live event log.</summary>
    private async Task<object?> RecordAsync<T>(SlackEventItemDto ev, SlackEventKind kind, Func<Task<T>> process) where T : class
    {
        var started = _time.GetTimestamp();
        try
        {
            var result = await process();
            var (outcome, detail, ids) = result switch
            {
                // A file that could not be downloaded is a failure the organisers should see, even when other files were stored
                ProcessSlackMessageResult { FailedDownloads.Count: > 0 } m => (SlackEventOutcome.Failed,
                    $"{m.Message}; download mislukt: {string.Join(", ", m.FailedDownloads)}", m.CreatedSubmissionIds),
                ProcessSlackMessageResult m => (m.Success ? SlackEventOutcome.Processed : SlackEventOutcome.Ignored, m.Message, m.CreatedSubmissionIds),
                ProcessSlackReactionResult r => (
                    r.Success ? SlackEventOutcome.Processed : r.IsFailure ? SlackEventOutcome.Failed : SlackEventOutcome.Ignored,
                    r.Message,
                    (IReadOnlyList<Guid>)(r.SubmissionId is { } id ? new[] { id } : Array.Empty<Guid>())),
                _ => (SlackEventOutcome.Processed, "", (IReadOnlyList<Guid>)Array.Empty<Guid>())
            };
            AddToLog(ev, kind, started, outcome, detail, ids);
            await TrackAsync(ev, kind, outcome == SlackEventOutcome.Failed ? detail : null);
            return result;
        }
        catch (Exception ex)
        {
            AddToLog(ev, kind, started, SlackEventOutcome.Failed, $"Fout: {ex.Message}", Array.Empty<Guid>());
            await TrackAsync(ev, kind, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// The same event, delivered again or run again from the events page, has the same key. Reactions share one key
    /// per message: running one again reads the message's reactions as Slack shows them now.
    /// </summary>
    public static string FailedEventKey(SlackEventItemDto ev, SlackEventKind kind) => kind switch
    {
        SlackEventKind.ReactionAdded or SlackEventKind.ReactionRemoved => $"Reaction:{ev.Item?.Channel}:{ev.Item?.Ts}",
        SlackEventKind.Deleted => $"Deleted:{ev.Channel}:{ev.DeletedTs ?? ev.Message?.Ts}",
        SlackEventKind.Edited => $"Edited:{ev.Channel}:{ev.Message?.Ts}",
        _ => $"{kind}:{ev.Channel}:{ev.Ts}"
    };

    /// <summary>
    /// Keeps a failed event for running it again, or marks an earlier failure of it as resolved.
    /// Uses its own scope: after a failed save the workflow's database context may still hold the broken changes.
    /// </summary>
    private async Task TrackAsync(SlackEventItemDto ev, SlackEventKind kind, string? error)
    {
        if (_scopes == null) return;

        var key = FailedEventKey(ev, kind);
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var failures = scope.ServiceProvider.GetRequiredService<IFailedSlackEventRepository>();
            if (error == null)
            {
                await failures.MarkResolvedAsync(key);
                return;
            }

            var reacted = kind is SlackEventKind.ReactionAdded or SlackEventKind.ReactionRemoved;
            await failures.RecordFailureAsync(
                key,
                kind.ToString(),
                reacted ? ev.Item?.Channel : ev.Channel,
                reacted ? ev.Item?.Ts : ev.Ts ?? ev.DeletedTs ?? ev.Message?.Ts,
                JsonSerializer.Serialize(ev),
                error);
        }
        catch (Exception ex)
        {
            // Never hides what happened to the event itself
            _logger.LogError(ex, "Could not keep track of failed Slack event {Key}", key);
        }
    }

    private void AddToLog(SlackEventItemDto ev, SlackEventKind kind, long started, SlackEventOutcome outcome, string detail, IReadOnlyList<Guid> submissionIds)
    {
        // An edit carries the new message, a deletion the old one
        var message = ev.Message ?? ev.PreviousMessage;
        _eventLog.Add(new SlackEventLogEntry(
            Guid.NewGuid(),
            _time.GetUtcNow(),
            _time.GetElapsedTime(started),
            kind,
            ev.Channel ?? ev.Item?.Channel,
            ev.User ?? message?.User,
            ev.Text ?? message?.Text,
            ev.Reaction,
            ev.Files?.Count ?? message?.Files?.Count ?? 0,
            outcome,
            detail,
            submissionIds));
    }
}
