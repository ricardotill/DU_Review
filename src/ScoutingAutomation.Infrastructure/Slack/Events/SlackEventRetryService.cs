using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Slack.Events;

/// <summary>One retry at a time across the app: two admins clicking at once would process the same event twice.</summary>
public sealed class SlackRetryLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <param name="Resolved">True when the event went through this time.</param>
public sealed record SlackEventRetryResult(bool Resolved, string Message);

/// <summary>
/// Runs a failed Slack event again, so one missed message or review does not need a rebuild from Slack.
/// The dispatcher marks the stored failure as resolved, or counts one more attempt.
/// </summary>
public class SlackEventRetryService
{
    private readonly IFailedSlackEventRepository _failures;
    private readonly ISlackEventDispatcher _dispatcher;
    private readonly ISubmissionWorkflowService _workflow;
    private readonly ISubmissionRepository _submissions;
    private readonly ISlackClient _slackClient;
    private readonly SlackProcessingGate _gate;
    private readonly SlackChannelLocks _channels;
    private readonly SlackEventLog _eventLog;
    private readonly RuntimeSettingsService _runtime;
    private readonly SlackRetryLock _retryLock;
    private readonly ILogger<SlackEventRetryService> _logger;

    public const string Busy = "Er loopt al een herhaling; wacht tot die klaar is";

    public SlackEventRetryService(
        IFailedSlackEventRepository failures,
        ISlackEventDispatcher dispatcher,
        ISubmissionWorkflowService workflow,
        ISubmissionRepository submissions,
        ISlackClient slackClient,
        SlackProcessingGate gate,
        SlackChannelLocks channels,
        SlackEventLog eventLog,
        RuntimeSettingsService runtime,
        SlackRetryLock retryLock,
        ILogger<SlackEventRetryService> logger)
    {
        _failures = failures;
        _dispatcher = dispatcher;
        _workflow = workflow;
        _submissions = submissions;
        _slackClient = slackClient;
        _gate = gate;
        _channels = channels;
        _eventLog = eventLog;
        _runtime = runtime;
        _retryLock = retryLock;
        _logger = logger;
    }

    public Task<IReadOnlyList<FailedSlackEvent>> GetPendingAsync(CancellationToken ct = default) =>
        _failures.GetPendingAsync(ct);

    public Task DismissAsync(Guid id, CancellationToken ct = default) =>
        _failures.DismissAsync(id, ct);

    public async Task<SlackEventRetryResult> RetryAsync(Guid id, CancellationToken ct = default)
    {
        if (!await _retryLock.Semaphore.WaitAsync(0, ct)) return new SlackEventRetryResult(false, Busy);
        try
        {
            return await RetryOneAsync(id, ct);
        }
        finally
        {
            _retryLock.Semaphore.Release();
        }
    }

    private async Task<SlackEventRetryResult> RetryOneAsync(Guid id, CancellationToken ct)
    {
        // A rebuild reads everything from Slack again: running an event in the middle of it would wait minutes
        if (_gate.IsClosed)
        {
            return new SlackEventRetryResult(false, "Er loopt een rebuild; probeer het daarna opnieuw");
        }

        var failure = await _failures.GetByIdAsync(id, ct);
        if (failure == null || failure.Status != FailedSlackEventStatus.Pending)
        {
            return new SlackEventRetryResult(true, "Al afgehandeld");
        }

        var reaction = failure.Kind is nameof(SlackEventKind.ReactionAdded) or nameof(SlackEventKind.ReactionRemoved);

        // Manual mode does not remember which files it saved: running a message again would save them all again
        if (!reaction && _runtime.ManualMode)
        {
            return new SlackEventRetryResult(false, "Handmatige modus staat aan: berichten worden dan niet opnieuw verwerkt");
        }

        SlackEventItemDto? ev;
        try
        {
            ev = JsonSerializer.Deserialize<SlackEventItemDto>(failure.PayloadJson);
        }
        catch (JsonException)
        {
            ev = null;
        }

        if (ev == null)
        {
            return new SlackEventRetryResult(false, "Het opgeslagen event kon niet worden gelezen");
        }

        _logger.LogInformation("Running failed Slack event {Key} again (attempt {Attempt})", failure.EventKey, failure.Attempts + 1);
        try
        {
            if (reaction)
            {
                await SyncReviewAsync(failure, ct);
            }
            else
            {
                await _dispatcher.DispatchAsync(new SlackEventEnvelopeDto { Type = "event_callback", Event = ev }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The dispatcher has stored the new error
            _logger.LogWarning(ex, "Failed Slack event {Key} failed again", failure.EventKey);
            return new SlackEventRetryResult(false, ex.Message);
        }

        var after = await _failures.GetByIdAsync(id, ct);
        if (after?.Status == FailedSlackEventStatus.Pending)
        {
            return new SlackEventRetryResult(false, after.Error);
        }

        if (!reaction)
        {
            await ApplyCurrentReviewAsync(failure, ct);
        }

        return new SlackEventRetryResult(true, "Opnieuw verwerkt");
    }

    /// <summary>
    /// A review emoji placed while the message could not be stored is not kept as a failure of its own: there was
    /// nothing to review yet. Now that the message is stored, its reactions are read from Slack as they are now.
    /// </summary>
    private async Task ApplyCurrentReviewAsync(FailedSlackEvent failure, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(failure.ChannelId) || string.IsNullOrWhiteSpace(failure.MessageTs) || !_slackClient.IsConnected) return;

        try
        {
            using var _ = await _gate.EnterAsync(ct);
            using var __ = await _channels.EnterAsync(failure.ChannelId, ct);

            if ((await _submissions.GetBySlackMessageIdAsync(failure.ChannelId, failure.MessageTs, ct)).Count == 0) return;

            var reactions = await _slackClient.GetReactionsAsync(failure.ChannelId, failure.MessageTs, ct);
            if (reactions == null) return;

            if (await _workflow.SyncReviewAsync(failure.ChannelId, failure.MessageTs, reactions, ct))
            {
                _eventLog.AddNotice(SlackEventKind.System, SlackEventOutcome.Processed,
                    $"Beoordeling van bericht {failure.MessageTs} uit Slack overgenomen na opnieuw verwerken", channel: failure.ChannelId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // The message is stored; the catch-up picks up the review on its next run
            _logger.LogWarning(ex, "Could not read the review of message {Ts} in {Channel} after running it again", failure.MessageTs, failure.ChannelId);
        }
    }

    /// <returns>How many of the open failures went through.</returns>
    /// <returns>How many of the open failures went through, or null when another retry was already running.</returns>
    public async Task<int?> RetryAllPendingAsync(CancellationToken ct = default)
    {
        if (!await _retryLock.Semaphore.WaitAsync(0, ct)) return null;
        try
        {
            var resolved = 0;

            // Oldest first: a message must be stored before a review of it can apply
            foreach (var failure in await _failures.GetPendingAsync(ct))
            {
                if ((await RetryOneAsync(failure.Id, ct)).Resolved) resolved++;
            }

            return resolved;
        }
        finally
        {
            _retryLock.Semaphore.Release();
        }
    }

    /// <summary>
    /// A reaction is not replayed as it came: the reviewer may have changed it since. Like the catch-up, the
    /// message's reactions are read from Slack as they are now, and dashboard overrides are left alone.
    /// </summary>
    private async Task SyncReviewAsync(FailedSlackEvent failure, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(failure.ChannelId) || string.IsNullOrWhiteSpace(failure.MessageTs))
        {
            await _failures.RecordFailureAsync(failure.EventKey, failure.Kind, failure.ChannelId, failure.MessageTs, failure.PayloadJson,
                "Reactie zonder kanaal of tijdstempel van het bericht", ct);
            return;
        }

        using var _ = await _gate.EnterAsync(ct);
        using var __ = await _channels.EnterAsync(failure.ChannelId, ct);

        // The message itself may have failed too: its review can only apply once it is stored
        if ((await _submissions.GetBySlackMessageIdAsync(failure.ChannelId, failure.MessageTs, ct)).Count == 0)
        {
            await _failures.RecordFailureAsync(failure.EventKey, failure.Kind, failure.ChannelId, failure.MessageTs, failure.PayloadJson,
                $"Geen inzending voor kanaal {failure.ChannelId}, ts {failure.MessageTs}; draai eerst het bericht opnieuw", ct);
            return;
        }

        var reactions = await _slackClient.GetReactionsAsync(failure.ChannelId, failure.MessageTs, ct);
        if (reactions == null && _slackClient.IsConnected)
        {
            await _failures.RecordFailureAsync(failure.EventKey, failure.Kind, failure.ChannelId, failure.MessageTs, failure.PayloadJson,
                "Reacties konden niet uit Slack worden gelezen", ct);
            return;
        }

        var changed = await _workflow.SyncReviewAsync(failure.ChannelId, failure.MessageTs, reactions, ct);
        await _failures.MarkResolvedAsync(failure.EventKey, ct);
        _eventLog.AddNotice(SlackEventKind.System, SlackEventOutcome.Processed,
            changed
                ? $"Mislukte reactie opnieuw verwerkt: beoordeling van bericht {failure.MessageTs} bijgewerkt"
                : $"Mislukte reactie opnieuw verwerkt: beoordeling van bericht {failure.MessageTs} klopt al",
            channel: failure.ChannelId);
    }
}
