using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Models;

public class SubmissionFilter
{
    public Guid? GroupId { get; set; }
    public int? AssignmentNumber { get; set; }
    public SubmissionStatus? Status { get; set; }
    public string? Reviewer { get; set; }

    /// <summary>Reviewer IDs whose name matches <see cref="Reviewer"/>; reviews by them match as well.</summary>
    public IReadOnlyList<string> ReviewerIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset? FromDate { get; set; }
    public DateTimeOffset? ToDate { get; set; }
    public string? SearchTerm { get; set; }
}
