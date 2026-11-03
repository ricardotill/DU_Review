namespace ScoutingAutomation.Domain.Entities;

/// <summary>
/// A place the Avonturiers can check in at. The number is the one teams write ("Locatie 7"), and also the order in
/// which locations open: a group has locations 1 through <see cref="Group.OpenLocations"/>.
/// </summary>
public class Location
{
    public int Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    /// <summary>Optional extra help shown on the map.</summary>
    public string? Hint { get; set; }
}
