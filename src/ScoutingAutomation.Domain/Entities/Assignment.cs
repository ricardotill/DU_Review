using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Entities;

public class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; }
    public int StandardPoints { get; set; }
    public int AwesomePoints { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int PointsFor(SubmissionStatus status) => status switch
    {
        SubmissionStatus.Awesome => AwesomePoints,
        SubmissionStatus.Approved => StandardPoints,
        _ => 0
    };
}
