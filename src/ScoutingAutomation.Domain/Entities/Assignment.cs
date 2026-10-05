using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Entities;

public class Assignment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>The number ("12") or, for a special assignment, the letters ("A"); see <see cref="Models.AssignmentCode"/>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Short name from the points file; null when it gives none.</summary>
    public string? Title { get; set; }

    /// <summary>What the groups must do, from the points file; null when it gives none.</summary>
    public string? Description { get; set; }
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
