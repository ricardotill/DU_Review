using Microsoft.Extensions.Logging;
using ScoutingAutomation.Contracts.Slack;

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
    private readonly ILogger<SlackEventDispatcher> _logger;

    public SlackEventDispatcher(ISubmissionWorkflowService workflowService, SlackProcessingGate gate, ILogger<SlackEventDispatcher> logger)
    {
        _workflowService = workflowService;
        _gate = gate;
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

        // Messages with uploads arrive with subtype "file_share"; a thread reply also sent to the channel is a "thread_broadcast"
        if (ev.Type == "message" && (string.IsNullOrEmpty(ev.Subtype) || ev.Subtype is "file_share" or "thread_broadcast"))
        {
            _logger.LogInformation("Processing Slack message event in channel {Channel}", ev.Channel);
            return await _workflowService.ProcessSlackMessageAsync(ev, ct);
        }

        if (ev.Type == "message" && ev.Subtype == "message_changed")
        {
            _logger.LogInformation("Processing edited Slack message {Ts} in channel {Channel}", ev.Message?.Ts, ev.Channel);
            return await _workflowService.ProcessSlackMessageChangedAsync(ev, ct);
        }

        if (ev.Type == "message" && ev.Subtype == "message_deleted")
        {
            _logger.LogInformation("Processing deleted Slack message {Ts} in channel {Channel}", ev.DeletedTs, ev.Channel);
            return await _workflowService.ProcessSlackMessageDeletedAsync(ev, ct);
        }

        if (ev.Type == "reaction_added")
        {
            _logger.LogInformation("Processing Slack reaction_added '{Reaction}' in channel {Channel}", ev.Reaction, ev.Item?.Channel);
            return await _workflowService.ProcessSlackReactionAsync(ev, ct);
        }

        if (ev.Type == "reaction_removed")
        {
            _logger.LogInformation("Processing Slack reaction_removed '{Reaction}' in channel {Channel}", ev.Reaction, ev.Item?.Channel);
            return await _workflowService.ProcessSlackReactionRemovedAsync(ev, ct);
        }

        return null;
    }
}
