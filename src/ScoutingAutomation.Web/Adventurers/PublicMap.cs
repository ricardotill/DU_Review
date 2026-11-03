using System.Security.Cryptography;
using System.Text;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Adventurers;

namespace ScoutingAutomation.Web.Adventurers;

/// <summary>
/// What a group's own map shows: only its open locations (1 through its number), coloured by its own check-ins.
/// Before the Avonturiers start <see cref="StartsAt"/> holds the time of day and there are no markers.
/// </summary>
public sealed record PublicMapData(string Group, IReadOnlyList<MapMarker> Markers, object Tiles, string Updated, string? StartsAt = null);

/// <summary>
/// The map a group opens through the link in Slack: no login, the secret token in the link is the key.
/// It shows no photos (those stay behind the login) and nothing of other groups.
/// </summary>
public static class PublicMap
{
    /// <summary>The map of the group with this token; null when no group has it or the Avonturiers part is off.</summary>
    public static async Task<PublicMapData?> BuildAsync(IServiceProvider services, string? token, CancellationToken ct = default)
    {
        var runtime = services.GetRequiredService<RuntimeSettingsService>();
        if (!runtime.AdventurersEnabled || string.IsNullOrWhiteSpace(token)) return null;

        var groups = await services.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
        var group = groups.FirstOrDefault(g => Matches(g.MapToken, token));
        if (group == null) return null;

        var options = runtime.AdventurersOptions;
        var tiles = new { tileUrl = options.MapTileUrl, attribution = options.MapAttribution };
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var updated = $"Bijgewerkt om {now.ToGameTime():HH:mm:ss}";
        // Nothing of the locations before the Avonturiers start
        if (runtime.AdventurersStartAt is { } start && now < start)
        {
            return new PublicMapData(group.Name, Array.Empty<MapMarker>(), tiles, updated, start.ToGameTime().ToString("HH:mm"));
        }

        var open = services.GetRequiredService<AssignmentUnlockService>().OpenLocationsOf(group);
        var shown = (await services.GetRequiredService<ILocationRepository>().GetAllAsync(ct)).Where(l => l.Number <= open).ToList();
        var submissions = await services.GetRequiredService<ISubmissionRepository>()
            .SearchSubmissionsAsync(new Domain.Models.SubmissionFilter { GroupId = group.Id, Kind = Domain.Enums.SubmissionKind.CheckIn }, ct);

        var super = runtime.SuperLocations;
        var markers = shown
            .Select(location =>
            {
                var state = AdventurersMap.StateOf(submissions, group.Id, location.Number);
                return MapMarker.For(location, state.Status.Color(), new[] { MapMarker.StatusLine(state) }, super: super.Contains(location.Number));
            })
            .ToList();

        return new PublicMapData(group.Name, markers, tiles, updated);
    }

    // The same time whatever part of the token is right, so it cannot be guessed piece by piece
    private static bool Matches(string? expected, string actual) =>
        expected != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));

    /// <summary>The data of a group's map for its page, which reads it again every half minute.</summary>
    public static IEndpointRouteBuilder MapPublicMapEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/kaart/{token}", async (string token, HttpContext http) =>
        {
            var data = await BuildAsync(http.RequestServices, token, http.RequestAborted);
            http.Response.Headers.CacheControl = "no-store";
            return data == null ? Results.NotFound() : Results.Json(data);
        }).AllowAnonymous();

        return app;
    }
}
