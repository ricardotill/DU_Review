using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Entities;

public class Submission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    public Group? Group { get; set; }
    /// <summary>
    /// The assignment's code ("12" or "A"); for a check-in or theme the location or theme number.
    /// Null when the message gave no valid one.
    /// </summary>
    public string? AssignmentCode { get; set; }

    /// <summary>Toeristen assignment (the default, also for rows from before kinds existed), check-in or theme.</summary>
    public SubmissionKind Kind { get; set; } = SubmissionKind.Assignment;

    /// <summary>"12", "L7" or "T3".</summary>
    public string CodeLabel => Kind.Label(AssignmentCode);
    public string SlackChannelId { get; set; } = string.Empty;
    public string SlackMessageId { get; set; } = string.Empty;

    /// <summary>Slack's ID of the uploaded file; null for submissions registered before it was stored.</summary>
    public string? SlackFileId { get; set; }
    public string Submitter { get; set; } = string.Empty;

    /// <summary>The submitter's Slack name at the time of upload; null when it could not be fetched.</summary>
    public string? SubmitterName { get; set; }

    /// <summary>The name to show: the Slack name, or the member ID when there is none.</summary>
    public string SubmitterDisplayName => string.IsNullOrWhiteSpace(SubmitterName) ? Submitter : SubmitterName;
    public string FileName { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public SubmissionStatus Status { get; set; } = SubmissionStatus.Received;
    public string? MessageText { get; set; }
    public string? UnresolvedReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }

    public ICollection<Review> Reviews { get; set; } = new List<Review>();

    public void MarkPending(string storagePath, string? assignmentCode, string? unresolvedReason = null)
    {
        Status = SubmissionStatus.Pending;
        StoragePath = storagePath;
        AssignmentCode = assignmentCode;
        UnresolvedReason = unresolvedReason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Moves the submission to another assignment, e.g. after the scout corrected the number in Slack.</summary>
    public void Reassign(string assignmentCode, string storagePath, SubmissionKind? kind = null, string? unresolvedReason = null)
    {
        AssignmentCode = assignmentCode;
        Kind = kind ?? Kind;
        StoragePath = storagePath;
        UnresolvedReason = unresolvedReason;
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public Review TransitionStatus(SubmissionStatus newStatus, string newStoragePath, string reviewer, string? reactionEmoji = null, string? notes = null)
    {
        Status = newStatus;
        StoragePath = newStoragePath;
        UpdatedAt = DateTimeOffset.UtcNow;

        var review = new Review
        {
            SubmissionId = Id,
            Reviewer = reviewer,
            Status = newStatus,
            ReactionEmoji = reactionEmoji,
            Notes = notes,
            CreatedAt = DateTimeOffset.UtcNow
        };

        Reviews.Add(review);
        return review;
    }
}
