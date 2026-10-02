namespace ScoutingAutomation.Domain.Entities;

public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }
    public string SlackChannelId { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Points for the Avonturiers category, entered by hand; Slack plays no part in them.</summary>
    public int AdventurersPoints { get; set; }

    /// <summary>When <see cref="AdventurersPoints"/> last changed: where the score chart places them.</summary>
    public DateTimeOffset? AdventurersPointsUpdatedAt { get; set; }

    /// <summary>Points for the Reisbureau (pubquiz) category, entered by hand.</summary>
    public int TravelAgencyPoints { get; set; }

    /// <summary>When <see cref="TravelAgencyPoints"/> last changed.</summary>
    public DateTimeOffset? TravelAgencyPointsUpdatedAt { get; set; }

    public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}
