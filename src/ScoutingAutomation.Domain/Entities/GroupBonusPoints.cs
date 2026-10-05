namespace ScoutingAutomation.Domain.Entities;

/// <summary>
/// A group's points in one category entered by hand, such as a pubquiz; Slack plays no part in them.
/// Kept when the category is removed from the settings, so adding it back brings the points back.
/// </summary>
public class GroupBonusPoints
{
    public Guid GroupId { get; set; }

    /// <summary>The key of the category, as in the BonusCategories settings.</summary>
    public string CategoryKey { get; set; } = string.Empty;

    public int Points { get; set; }

    /// <summary>When the points last changed: where the score chart places them when there is no history.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    public Group? Group { get; set; }
}
