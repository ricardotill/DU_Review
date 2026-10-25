namespace ScoutingAutomation.Domain.Entities;

/// <summary>A place the Avonturiers can check in at. The number is the one teams write ("Locatie 7").</summary>
public class Location
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Optional extra help shown on the map.</summary>
    public string? Hint { get; set; }
}

/// <summary>A location unlocked for one group (by its Reisbureau): the group's map shows it.</summary>
public class LocationUnlock
{
    public Guid GroupId { get; set; }
    public int LocationNumber { get; set; }
    public DateTimeOffset UnlockedAt { get; set; }

    public Group? Group { get; set; }
}
