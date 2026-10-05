namespace ScoutingAutomation.Domain.Entities;

/// <summary>
/// One change to a group's hand-entered points, so the score chart shows every change at its own time.
/// The current value is in <see cref="GroupBonusPoints"/>; these rows are only the history.
/// </summary>
public class BonusPointsEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GroupId { get; set; }
    /// <summary>The key of the category, as in the BonusCategories settings.</summary>
    public string CategoryKey { get; set; } = string.Empty;

    /// <summary>The change: negative when points were taken away.</summary>
    public int Delta { get; set; }

    /// <summary>The category's value after this change.</summary>
    public int Total { get; set; }

    public DateTimeOffset ChangedAt { get; set; }

    public Group? Group { get; set; }
}
