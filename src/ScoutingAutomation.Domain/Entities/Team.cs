namespace ScoutingAutomation.Domain.Entities;

/// <summary>
/// A scouting group as it lasts from year to year. Each edition it takes part in has its own <see cref="Group"/> row
/// (channel, abbreviation, map link, submissions); a group account belongs to the team, so it sees every edition the
/// group played.
/// </summary>
/// <remarks>A team made together with a group gets the group's id, so in its first edition both ids are the same.</remarks>
public class Team
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name when the team was made; each edition's group has its own (current) name.</summary>
    public string Name { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
