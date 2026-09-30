using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Entities;

public class Review
{
    public Guid Id { get; set; }
    public Guid SubmissionId { get; set; }
    public Submission? Submission { get; set; }
    public string Reviewer { get; set; } = string.Empty;
    public SubmissionStatus Status { get; set; }
    public string? ReactionEmoji { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
