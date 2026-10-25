namespace ScoutingAutomation.Domain.Entities;

public class Group
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Abbreviation { get; set; }
    public string SlackChannelId { get; set; } = string.Empty;

    /// <summary>The Slack channel of the group's Avonturiers; null when it has none.</summary>
    public string? AdventurersSlackChannelId { get; set; }

    /// <summary>Secret part of the link to the group's map page, which opens without a login.</summary>
    public string? MapToken { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ICollection<Submission> Submissions { get; set; } = new List<Submission>();
}
