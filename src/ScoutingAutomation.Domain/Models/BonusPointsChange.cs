namespace ScoutingAutomation.Domain.Models;

/// <summary>New bonus points for one group, with the values they were changed from.</summary>
public sealed record BonusPointsChange(
    Guid GroupId,
    int PreviousAdventurers,
    int PreviousTravelAgency,
    int Adventurers,
    int TravelAgency)
{
    /// <summary>Above this a value is taken for a typo: no category hands out that many points.</summary>
    public const int MaxPoints = 1000;
}
