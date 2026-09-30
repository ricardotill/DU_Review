using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Infrastructure.Services;

public record ProcessSlackMessageResult(bool Success, string Message, IReadOnlyList<Guid> CreatedSubmissionIds)
{
    public IReadOnlyList<string> UnsupportedFiles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> FailedDownloads { get; init; } = Array.Empty<string>();

    public static ProcessSlackMessageResult Succeeded(IReadOnlyList<Guid> ids, string msg = "Processed successfully") =>
        new(true, msg, ids);
    public static ProcessSlackMessageResult Ignored(string reason) =>
        new(false, reason, Array.Empty<Guid>());
}

public record ProcessSlackReactionResult(bool Success, string Message, Guid? SubmissionId = null, SubmissionStatus? NewStatus = null)
{
    public static ProcessSlackReactionResult Succeeded(Guid submissionId, SubmissionStatus newStatus, string msg = "Reaction applied successfully") =>
        new(true, msg, submissionId, newStatus);
    public static ProcessSlackReactionResult Ignored(string reason) =>
        new(false, reason);
    public static ProcessSlackReactionResult Failed(string reason) =>
        new(false, reason);
}

/// <param name="Status">The status applied, or null when no reviewer reacted with a review emoji.</param>
/// <param name="Conflict">True when reviewers used emojis for different statuses.</param>
public record ImportedReviewResult(SubmissionStatus? Status, bool Conflict);

public interface ISubmissionWorkflowService
{
    Task<ProcessSlackMessageResult> ProcessSlackMessageAsync(SlackEventItemDto messageEvent, CancellationToken ct = default);
    Task<ProcessSlackReactionResult> ProcessSlackReactionAsync(SlackEventItemDto reactionEvent, CancellationToken ct = default);

    /// <summary>
    /// Processes a message read back from Slack's history: same rules as a live message,
    /// but without posting reactions or replies to Slack.
    /// </summary>
    Task<ProcessSlackMessageResult> ImportSlackMessageAsync(SlackEventItemDto message, CancellationToken ct = default);

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
