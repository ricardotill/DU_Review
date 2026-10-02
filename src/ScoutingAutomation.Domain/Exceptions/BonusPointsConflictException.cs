namespace ScoutingAutomation.Domain.Exceptions;

/// <summary>
/// Someone else changed a group's bonus points after they were loaded: nothing was saved, so their entry is not undone.
/// </summary>
public class BonusPointsConflictException : Exception
{
    public BonusPointsConflictException(string groupName)
        : base($"{groupName} is intussen door iemand anders gewijzigd; de nieuwe waarden staan nu in de tabel")
    {
        GroupName = groupName;
    }

    public string GroupName { get; }
}
