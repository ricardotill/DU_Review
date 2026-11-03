using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Web.Auth;

/// <summary>The start of links that an admin makes: <see cref="ScoutingSettings.PublicUrl"/>, else the address in the admin's browser.</summary>
/// <remarks>Links asked for without a login (forgotten password) only ever use <see cref="ScoutingSettings.PublicUrl"/>.</remarks>
public static class BaseUrl
{
    /// <param name="baseUri">For a live page: NavigationManager.BaseUri, the address in the admin's browser.</param>
    public static string Of(ScoutingSettings settings, string baseUri) =>
        (!string.IsNullOrWhiteSpace(settings.PublicUrl) ? settings.PublicUrl : baseUri).TrimEnd('/');
}
