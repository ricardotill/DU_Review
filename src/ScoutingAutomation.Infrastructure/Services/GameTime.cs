namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// The time zone the game is played in. File names and times on the pages use it,
/// so they are right even when the server runs in another time zone (e.g. UTC in the cloud).
/// </summary>
public static class GameTime
{
    public const string DefaultTimeZoneId = "Europe/Amsterdam";

    public static TimeZoneInfo TimeZone { get; private set; } = Find(DefaultTimeZoneId) ?? TimeZoneInfo.Local;

    /// <summary>Sets the game's time zone from <c>Scouting:TimeZone</c>.</summary>
    /// <returns>False when the ID is unknown; the server's own time zone is used then.</returns>
    public static bool Configure(string? timeZoneId)
    {
        var zone = Find(string.IsNullOrWhiteSpace(timeZoneId) ? DefaultTimeZoneId : timeZoneId.Trim());
        TimeZone = zone ?? TimeZoneInfo.Local;
        return zone != null;
    }

    /// <summary>The same moment, with the offset of the game's time zone.</summary>
    public static DateTimeOffset ToGameTime(this DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone);

    /// <summary>The game-time offset of a date and time chosen on a page, which has no time zone of its own.</summary>
    public static DateTimeOffset FromGameTime(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZone.GetUtcOffset(value));

    private static TimeZoneInfo? Find(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return null;
        }
    }
}
