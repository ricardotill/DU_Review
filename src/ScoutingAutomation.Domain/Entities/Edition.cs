namespace ScoutingAutomation.Domain.Entities;

/// <summary>
/// One year of the game: its groups (with their channels), assignments, locations and everything sent in. Exactly one
/// edition is active; only that one handles Slack, and the app shows it by default.
/// </summary>
public class Edition
{
    public const int MaxNameLength = 100;

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>"DU 2026".</summary>
    public string Name { get; set; } = string.Empty;

    public int Year { get; set; }

    public EditionStatus Status { get; set; } = EditionStatus.Draft;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The folder of this edition's group folders, inside the storage root; null for the storage root itself (the
    /// edition made from a database from before editions keeps its files where they were).
    /// </summary>
    public string? StorageFolder { get; set; }

    /// <summary>When the edition was archived; null while it is not.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>The final standing, saved when the edition was archived (JSON of <see cref="EditionStanding"/> rows).</summary>
    public string? FinalStandingJson { get; set; }

    /// <summary>The name a new edition gets when the app makes it by itself: "DU" and the year.</summary>
    public static string DefaultName(int year) => $"DU {year}";
}

/// <summary>One group's place in the final standing of an edition.</summary>
public sealed record EditionStanding(Guid TeamId, Guid GroupId, string GroupName, int Rank, int Score);

public enum EditionStatus
{
    /// <summary>Being prepared: not handling Slack yet.</summary>
    Draft,

    /// <summary>The edition being played now; at most one.</summary>
    Active,

    /// <summary>Played: kept to look back at, no longer changed.</summary>
    Archived
}

/// <summary>A row that belongs to one edition.</summary>
public interface IEditionScoped
{
    Guid EditionId { get; set; }
}
