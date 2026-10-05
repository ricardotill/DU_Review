using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Infrastructure.Workflow;

/// <summary>
/// One Slack message that waits for a review. One emoji reviews all its files, so the queue counts messages, not files.
/// </summary>
/// <param name="AlreadyCompleted">
/// The group already has this assignment approved or awesome in another message, so a review cannot add points.
/// </param>
public sealed record ReviewQueueItem(
    string ChannelId,
    string MessageTs,
    Group? Group,
    string? AssignmentCode,
    IReadOnlyList<Submission> Submissions,
    DateTimeOffset PostedAt,
    bool AlreadyCompleted)
{
    public Submission First => Submissions[0];

    /// <summary>Toeristen assignment, check-in or theme: one message has one kind.</summary>
    public SubmissionKind Kind => First.Kind;

    /// <summary>"12", "L7" or "T3".</summary>
    public string CodeLabel => First.CodeLabel;

    public TimeSpan WaitingFor(DateTimeOffset now) => now > PostedAt ? now - PostedAt : TimeSpan.Zero;
}

public static class ReviewQueue
{
    /// <summary>Every message that waits for a review, oldest first.</summary>
    public static async Task<IReadOnlyList<ReviewQueueItem>> LoadAsync(ISubmissionRepository repository, CancellationToken ct = default) =>
        Build(await repository.SearchSubmissionsAsync(new SubmissionFilter(), ct));

    /// <summary>The open messages among <paramref name="submissions"/>, oldest first.</summary>
    public static IReadOnlyList<ReviewQueueItem> Build(IEnumerable<Submission> submissions)
    {
        var all = submissions.ToList();
        var completed = all
            .Where(s => s.AssignmentCode != null && s.Status is SubmissionStatus.Approved or SubmissionStatus.Awesome)
            .Select(s => (s.GroupId, s.Kind, Code: s.AssignmentCode!))
            .ToHashSet();

        return all
            .Where(s => s.Status is SubmissionStatus.Pending or SubmissionStatus.Received)
            .GroupBy(s => (s.SlackChannelId, s.SlackMessageId))
            .Select(message =>
            {
                var files = message.OrderBy(s => s.StoragePath, StringComparer.Ordinal).ToList();
                var first = files[0];
                return new ReviewQueueItem(
                    message.Key.SlackChannelId,
                    message.Key.SlackMessageId,
                    first.Group,
                    first.AssignmentCode,
                    files,
                    files.Min(s => s.CreatedAt),
                    first.AssignmentCode is { } code && completed.Contains((first.GroupId, first.Kind, code)));
            })
            .OrderBy(i => i.PostedAt)
            .ToList();
    }

    /// <summary>"12 min", "1 u 05 min": a short waiting time for a page or a Slack message.</summary>
    public static string FormatWait(TimeSpan wait) =>
        wait.TotalMinutes < 1 ? "< 1 min"
        : wait.TotalHours < 1 ? $"{(int)wait.TotalMinutes} min"
        : $"{(int)wait.TotalHours} u {wait.Minutes:00} min";
}
