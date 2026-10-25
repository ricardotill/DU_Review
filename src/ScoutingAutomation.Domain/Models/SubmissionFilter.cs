using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Models;

public class SubmissionFilter
{
    public Guid? GroupId { get; set; }
    public string? AssignmentCode { get; set; }

    /// <summary>Only this kind; every kind when null.</summary>
    public SubmissionKind? Kind { get; set; }
    public SubmissionStatus? Status { get; set; }
    public string? Reviewer { get; set; }

    /// <summary>Reviewer IDs whose name matches <see cref="Reviewer"/>; reviews by them match as well.</summary>
    public IReadOnlyList<string> ReviewerIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset? FromDate { get; set; }
    public DateTimeOffset? ToDate { get; set; }
    public string? SearchTerm { get; set; }
}
