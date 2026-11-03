using ScoutingAutomation.Contracts.Configuration;

namespace ScoutingAutomation.Web.Auth;

/// <summary>The start of links in mails: <see cref="ScoutingSettings.PublicUrl"/>, else the address the request came in on.</summary>
public static class BaseUrl
{
    public static string Of(ScoutingSettings settings, HttpRequest request) =>
        !string.IsNullOrWhiteSpace(settings.PublicUrl) ? settings.PublicUrl.TrimEnd('/') : $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <param name="baseUri">For a live page: NavigationManager.BaseUri, the address in the admin's browser.</param>
    public static string Of(ScoutingSettings settings, string baseUri) =>
        (!string.IsNullOrWhiteSpace(settings.PublicUrl) ? settings.PublicUrl : baseUri).TrimEnd('/');
}
