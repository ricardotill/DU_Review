using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Infrastructure.Services;

public record ProcessSlackMessageResult(bool Success, string Message, IReadOnlyList<Guid> CreatedSubmissionIds)
{
    public IReadOnlyList<string> UnsupportedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedDownloads { get; init; } = Array.Empty<string>();

    /// <summary>Names of the files saved in storage.</summary>
    public IReadOnlyList<string> StoredFiles { get; init; } = Array.Empty<string>();

    public static ProcessSlackMessageResult Succeeded(IReadOnlyList<Guid> ids, string msg = "Processed successfully") =>
        new(true, msg, ids);
    public static ProcessSlackMessageResult Ignored(string reason) =>
        new(false, reason, Array.Empty<Guid>());
}

public record ProcessSlackReactionResult(bool Success, string Message, Guid? SubmissionId = null, SubmissionStatus? NewStatus = null)
{
    /// <summary>True when the event could not be handled, as opposed to a reaction that is deliberately ignored.</summary>
    public bool IsFailure { get; init; }

    public static ProcessSlackReactionResult Succeeded(Guid submissionId, SubmissionStatus newStatus, string msg = "Reaction applied successfully") =>
        new(true, msg, submissionId, newStatus);
    public static ProcessSlackReactionResult Ignored(string reason) =>
        new(false, reason);
    public static ProcessSlackReactionResult Failed(string reason) =>
        new(false, reason) { IsFailure = true };
}

/// <param name="Status">The status applied, or null when no reviewer reacted with a review emoji.</param>
/// <param name="Conflict">True when reviewers used emojis for different statuses.</param>
public record ImportedReviewResult(SubmissionStatus? Status, bool Conflict);

public interface ISubmissionWorkflowService
{
    Task<ProcessSlackMessageResult> ProcessSlackMessageAsync(SlackEventItemDto messageEvent, CancellationToken ct = default);
    Task<ProcessSlackReactionResult> ProcessSlackReactionAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default);

    /// <summary>
    /// Handles a reviewer taking back a review emoji: re-reads the reactions still on the message and
    /// applies the best remaining review, or makes the submission Pending again when none is left.
    /// </summary>
    Task<ProcessSlackReactionResult> ProcessSlackReactionRemovedAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default);

    /// <summary>
    /// Handles an edited message ("message_changed"). A message that was skipped for lacking an assignment number
    /// is registered once the number is added; a corrected number moves its submissions to that assignment.
    /// Files deleted from the message are removed like a deleted message.
    /// </summary>
    Task<ProcessSlackMessageResult> ProcessSlackMessageChangedAsync(SlackEventItemDto changeEvent, CancellationToken ct = default);

    /// <summary>
    /// Handles a deleted message ("message_deleted"): its submissions no longer count. Their files are kept in "_deleted".
    /// </summary>
    Task<ProcessSlackMessageResult> ProcessSlackMessageDeletedAsync(SlackEventItemDto deleteEvent, CancellationToken ct = default);

    /// <summary>
    /// Processes a message read back from Slack's history: same rules as a live message,
    /// but without posting reactions or replies to Slack.
    /// </summary>
    Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default);

    /// <summary>
    /// Processes a message that may have been missed while the app was not connected. Same as a live message,
    /// including feedback to the group; Slack shows feedback only once, so a message seen before gets none again.
    /// </summary>
    Task<ProcessSlackMessageResult> ProcessMissedMessageAsync(SlackEventItemDto message, CancellationToken ct = default);

    /// <summary>
    /// Brings a message's submissions in line with the reviews on it in Slack now. When Slack shows no review,
    /// a Slack review is undone, but a dashboard override is kept: it stands in for Slack while Slack had problems.
    /// <paramref name="reactions"/> may be old: before a status changes, the reactions are read from Slack again,
    /// and when that fails nothing changes.
    /// </summary>
    /// <returns>True when a status changed.</returns>
    Task<bool> SyncReviewAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default);

    /// <summary>
    /// Applies the reviewer reactions currently on a message. Slack does not say in which order
    /// reactions were placed, so when reviewers disagree the best status wins.
    /// </summary>
    Task<ImportedReviewResult> ApplyImportedReactionsAsync(string channelId, string messageTs, IReadOnlyList<SlackMessageReactionDto>? reactions, CancellationToken ct = default);

    /// <summary>
    /// Emergency override of a submission's status from the dashboard. Normal reviews happen in Slack.
    /// </summary>
    Task<Submission> OverrideStatusAsync(Guid submissionId, SubmissionStatus newStatus, string reason, CancellationToken ct = default);
}
