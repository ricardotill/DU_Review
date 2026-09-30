namespace ScoutingAutomation.Domain.Enums;

public static class SubmissionStatusExtensions
{
    // Resubmissions replace earlier attempts: the best status wins, so a rejected
    // attempt followed by an approved one counts as approved only
    public static int Rank(this SubmissionStatus status) => status switch
    {
        SubmissionStatus.Awesome => 3,
        SubmissionStatus.Approved => 2,
        SubmissionStatus.Rejected => 0,
        _ => 1 // Received / Pending
    };

    /// <summary>
    /// The status that counts for a group's assignment, given all its submissions.
    /// </summary>
    public static SubmissionStatus Best(this IEnumerable<SubmissionStatus> statuses) =>
        statuses.MaxBy(Rank);
}
