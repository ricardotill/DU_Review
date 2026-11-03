using ScoutingAutomation.Infrastructure.Editions;
using ScoutingAutomation.Web.Auth;

namespace ScoutingAutomation.Web.Export;

/// <summary>An edition as one ZIP with its pictures, and such a ZIP back as a new edition. Admin only.</summary>
public static class EditionEndpoints
{
    /// <summary>The largest ZIP an admin can upload.</summary>
    public const long MaxUploadBytes = 20L * 1024 * 1024 * 1024;

    public static void MapEditionEndpoints(this IEndpointRouteBuilder api)
    {
        var editions = api.MapGroup("/editions").RequireAuthorization(p => p.RequireRole(AppAuth.AdminRole));
        editions.MapGet("/{id:guid}/export.zip", ExportAsync);
        editions.MapPost("/import", ImportAsync)
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxUploadBytes))
            .WithFormOptions(multipartBodyLengthLimit: MaxUploadBytes);
    }

    private static async Task ExportAsync(Guid id, HttpContext http, EditionArchive archive, ILoggerFactory loggers)
    {
        var edition = await archive.FindAsync(id, http.RequestAborted);
        if (edition == null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = EditionArchive.FileName(edition) }.ToString();
        // Like the files export: the data goes out asynchronously, each entry's small trailer synchronously
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        try
        {
            await archive.ExportAsync(id, http.Response.Body, http.RequestAborted);
        }
        catch (Exception ex) when (!http.RequestAborted.IsCancellationRequested)
        {
            // Started already: a broken connection makes the browser report a failed download instead of a cut-off ZIP
            loggers.CreateLogger(typeof(EditionEndpoints)).LogError(ex, "Export of edition {Edition} failed halfway; the download was broken off", edition.Name);
            http.Abort();
        }
    }

    /// <summary>Takes the uploaded ZIP (form field "file") and goes back to Beheer with what happened.</summary>
    private static async Task<IResult> ImportAsync(IFormFile? file, EditionArchive archive, CancellationToken ct)
    {
        if (file == null || file.Length == 0) return Back(error: "Kies een bestand om te importeren.");

        // A ZIP is read from its end: the upload goes to a file first
        var temp = Path.Combine(Path.GetTempPath(), $"du-import-{Guid.NewGuid():N}.zip");
        try
        {
            await using (var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await file.CopyToAsync(target, ct);
            }

            var (edition, error) = await archive.ImportAsync(temp, ct);
            return edition == null ? Back(error: error) : Back(imported: edition.Name);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static IResult Back(string? error = null, string? imported = null) =>
        Results.Redirect(error != null
            ? $"/admin?importfout={Uri.EscapeDataString(error)}#edities"
            : $"/admin?geimporteerd={Uri.EscapeDataString(imported!)}#edities");
}
