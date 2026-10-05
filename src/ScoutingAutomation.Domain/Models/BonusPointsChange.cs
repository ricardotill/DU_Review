namespace ScoutingAutomation.Domain.Models;

/// <summary>New points for one group in one category, with the value it was changed from.</summary>
public sealed record BonusPointsChange(Guid GroupId, string CategoryKey, int Previous, int Points)
{
    /// <summary>Above this a value is taken for a typo: no category hands out that many points.</summary>
    public const int MaxPoints = 1000;
}
