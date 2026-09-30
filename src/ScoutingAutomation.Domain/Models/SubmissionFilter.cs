using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Models;

public class SubmissionFilter
{
    public Guid? GroupId { get; set; }
    public int? AssignmentNumber { get; set; }
    public SubmissionStatus? Status { get; set; }
    public string? Reviewer { get; set; }
    public DateTimeOffset? FromDate { get; set; }
    public DateTimeOffset? ToDate { get; set; }
    public string? SearchTerm { get; set; }
}
