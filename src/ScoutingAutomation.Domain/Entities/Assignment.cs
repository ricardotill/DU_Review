using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Entities;

public class Assignment : IEditionScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EditionId { get; set; }
    /// <summary>The number ("12") or, for a special assignment, the letters ("A"); see <see cref="Models.AssignmentCode"/>.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Short name from the points file; null when it gives none.</summary>
    public string? Title { get; set; }

    /// <summary>What the groups must do, from the points file; null when it gives none.</summary>
    public string? Description { get; set; }
    public int StandardPoints { get; set; }
    public int AwesomePoints { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the jury last changed the title, description or points in the app; null when they come from the points
    /// file. An edited assignment keeps its values when the app starts again.
    /// </summary>
    public DateTimeOffset? EditedAt { get; set; }

    public int PointsFor(SubmissionStatus status) => status switch
    {
        SubmissionStatus.Awesome => AwesomePoints,
        SubmissionStatus.Approved => StandardPoints,
        _ => 0
    };
}
