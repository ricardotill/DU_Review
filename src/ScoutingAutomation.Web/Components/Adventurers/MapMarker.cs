using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Web.Components.Adventurers;

/// <summary>A photo shown in a marker's popup.</summary>
/// <param name="Link">Where a click goes; the photo itself when null.</param>
public sealed record MapPhoto(string Url, string Caption, bool IsImage, string? Link = null);

/// <summary>One location on a map, as map.js draws it. Serialised in camelCase for the browser.</summary>
public sealed record MapMarker(double Lat, double Lng, int Number, string Name, string? Hint, string Color,
    IReadOnlyList<string> Lines, IReadOnlyList<MapPhoto> Photos, bool Super = false)
{
    /// <param name="super">A super location ('supercoördinaat'): the map shows a star.</param>
    public static MapMarker For(Location location, string color, IReadOnlyList<string> lines, IReadOnlyList<MapPhoto>? photos = null, bool super = false) =>
        new(location.Latitude, location.Longitude, location.Number, location.Name, location.Hint, color, lines, photos ?? Array.Empty<MapPhoto>(), super);

    /// <summary>The line under a location's name: its status and when the group last posted there.</summary>
    public static string StatusLine(LocationState state) => state.LastPostAt is { } at
        ? $"{state.Status.Label()} (laatste foto {at.ToGameTime():HH:mm})"
        : state.Status.Label();

    /// <summary>The photo of a check-in, for the jury: a click opens the submission.</summary>
    public static MapPhoto PhotoOf(Submission submission, string caption) =>
        new($"/api/files/{submission.Id}", caption, submission.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase), $"/submissions/{submission.Id}");
}
