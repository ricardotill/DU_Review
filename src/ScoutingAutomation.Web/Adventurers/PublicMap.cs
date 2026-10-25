using System.Security.Cryptography;
using System.Text;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Adventurers;

namespace ScoutingAutomation.Web.Adventurers;

/// <summary>What a group's own map shows: only its unlocked locations, coloured by its own check-ins.</summary>
public sealed record PublicMapData(string Group, IReadOnlyList<MapMarker> Markers, object Tiles, string Updated);

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

        var locations = services.GetRequiredService<ILocationRepository>();
        var unlocked = (await locations.GetUnlocksAsync(group.Id, ct)).Select(u => u.LocationNumber).ToHashSet();
        var shown = (await locations.GetAllAsync(ct)).Where(l => unlocked.Contains(l.Number)).ToList();
        var submissions = await services.GetRequiredService<ISubmissionRepository>()
            .SearchSubmissionsAsync(new Domain.Models.SubmissionFilter { GroupId = group.Id, Kind = Domain.Enums.SubmissionKind.CheckIn }, ct);

        var markers = shown
            .Select(location =>
            {
                var state = AdventurersMap.StateOf(submissions, group.Id, location.Number);
                return MapMarker.For(location, state.Status.Color(), new[] { MapMarker.StatusLine(state) });
            })
            .ToList();

        var options = runtime.AdventurersOptions;
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow().ToGameTime();
        return new PublicMapData(group.Name, markers, new { tileUrl = options.MapTileUrl, attribution = options.MapAttribution }, $"Bijgewerkt om {now:HH:mm:ss}");
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
