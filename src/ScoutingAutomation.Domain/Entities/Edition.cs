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

    /// <summary>The name a new edition gets when the app makes it by itself: "DU" and the year.</summary>
    public static string DefaultName(int year) => $"DU {year}";
}

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
