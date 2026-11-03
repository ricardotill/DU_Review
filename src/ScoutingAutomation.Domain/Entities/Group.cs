namespace ScoutingAutomation.Domain.Entities;

/// <summary>A group as it takes part in one edition; the same group in another year is another row of the same <see cref="Team"/>.</summary>
public class Group : IEditionScoped
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid EditionId { get; set; }
    public Edition? Edition { get; set; }

    /// <summary>The lasting group this row is part of; set to a new team (with this group's id) when left empty.</summary>
    public Guid TeamId { get; set; }
    public Team? Team { get; set; }

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
