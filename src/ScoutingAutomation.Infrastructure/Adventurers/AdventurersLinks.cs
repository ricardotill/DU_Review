using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Links to the Avonturiers pages that are put in Slack messages.</summary>
public static class AdventurersLinks
{
    /// <summary>The path of a group's map page, which needs no login: the token is the key.</summary>
    public static string MapPath(string token) => $"/kaart/{Uri.EscapeDataString(token)}";

    /// <summary>
    /// The full link to a group's map, or null when the app's public address (<c>Scouting:PublicUrl</c>) is not set
    /// or the group has no token yet.
    /// </summary>
    public static string? MapUrl(ScoutingSettings settings, Group group)
    {
        if (string.IsNullOrWhiteSpace(settings.PublicUrl) || string.IsNullOrWhiteSpace(group.MapToken)) return null;
        return settings.PublicUrl.TrimEnd('/') + MapPath(group.MapToken);
    }

    /// <summary>What <c>{map}</c> becomes when there is no link.</summary>
    public const string NoMapLink = "(de kaartlink volgt)";
}
