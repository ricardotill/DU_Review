using Microsoft.AspNetCore.StaticFiles;

namespace ScoutingAutomation.Web.Security;

/// <summary>
/// The content type to serve a stored submission with, from its file extension. Slack reports the type the
/// uploading phone claimed; a type a browser would run (HTML, SVG, scripts) is never passed on.
/// </summary>
public static class SafeContentType
{
    private static readonly FileExtensionContentTypeProvider Known = new();

    public const string Fallback = "application/octet-stream";

    public static string For(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || !Known.TryGetContentType(fileName, out var type))
        {
            return Fallback;
        }

        var safe = (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !type.StartsWith("image/svg", StringComparison.OrdinalIgnoreCase))
                   || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                   || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                   || type is "application/pdf" or "application/msword"
                       or "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
        return safe ? type : Fallback;
    }
}
