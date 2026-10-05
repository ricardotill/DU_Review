using System.Globalization;
using System.IO.Compression;
using System.Text;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Auth;

namespace ScoutingAutomation.Web.Export;

/// <summary>
/// Downloads for after the game: the scoreboard as CSV and the approved or awesome files as ZIP. Admin only.
/// </summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this IEndpointRouteBuilder api)
    {
        var export = api.MapGroup("/export").RequireAuthorization(p => p.RequireRole(KeyAuth.AdminRole));
        export.MapGet("/scores.csv", ScoresCsvAsync);
        export.MapGet("/files.zip", FilesZipAsync);
    }

    private static async Task<IResult> ScoresCsvAsync(ISubmissionRepository submissions, IGroupRepository groups, RuntimeSettingsService runtime, TimeProvider time)
    {
        var stats = await submissions.GetStatisticsAsync();
        var categories = runtime.BonusCategories;
        var groupsById = (await groups.GetAllAsync()).ToDictionary(g => g.Id);

        // Semicolons and a BOM: what Dutch Excel expects when the file is double-clicked
        var csv = new StringBuilder();
        csv.AppendJoin(';', new[] { "Plaats", "Groep", "Afkorting", "Punten" }
            .Concat(categories.Select(c => Field(c.Name)))
            .Concat(new[] { "Opdrachten", "Open", "Goedgekeurd", "Awesome", "Afgekeurd", "Laatste upload" }));
        csv.Append("\r\n");
        foreach (var g in stats.GroupStatistics.OrderBy(g => g.Rank).ThenBy(g => g.GroupName))
        {
            csv.AppendJoin(';', new object?[]
            {
                g.Rank,
                Field(g.GroupName),
                Field(groupsById.GetValueOrDefault(g.GroupId)?.Abbreviation),
                g.Score
            }
            .Concat(categories.Select(c => (object?)g.BonusPoints.GetValueOrDefault(c.Key)))
            .Concat(new object?[]
            {
                g.TotalSubmissions,
                g.PendingCount,
                g.ApprovedCount,
                g.AwesomeCount,
                g.RejectedCount,
                g.LastSubmissionAt is { } at ? at.ToGameTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : ""
            }));
            csv.Append("\r\n");
        }

        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
        return Results.File(bytes, "text/csv; charset=utf-8", $"DU scores {Today(time)}.csv");
    }

    /// <param name="status">"awesome" for awesome only; "approved" (the default) for approved and awesome.</param>
    /// <param name="group">Only this group; all groups when left out.</param>
    private static async Task FilesZipAsync(
        HttpContext http,
        ISubmissionRepository submissions,
        IFileStorageService storage,
        TimeProvider time,
        ILoggerFactory loggers,
        string? status,
        Guid? group)
    {
        var awesomeOnly = string.Equals(status, "awesome", StringComparison.OrdinalIgnoreCase);
        if (status != null && !awesomeOnly && !string.Equals(status, "approved", StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync("status must be 'approved' or 'awesome'");
            return;
        }

        var files = (await submissions.SearchSubmissionsAsync(new SubmissionFilter { GroupId = group }))
            .Where(s => s.Status == SubmissionStatus.Awesome || (!awesomeOnly && s.Status == SubmissionStatus.Approved))
            .OrderBy(s => s.Group?.Name).ThenBy(s => s.AssignmentCode, AssignmentCode.Comparer).ThenBy(s => s.CreatedAt)
            .ToList();

        var name = $"DU {(awesomeOnly ? "awesome" : "goedgekeurd")} {Today(time)}.zip";
        http.Response.ContentType = "application/zip";
        http.Response.Headers.ContentDisposition = new System.Net.Mime.ContentDisposition { FileName = name }.ToString();

        // Written straight to the response, so large videos never sit in memory. The file data goes out
        // asynchronously, but .NET still writes each entry's small trailer (its data descriptor) synchronously
        if (http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>() is { } body) body.AllowSynchronousIO = true;
        var ct = http.RequestAborted;
        var logger = loggers.CreateLogger(typeof(ExportEndpoints));
        try
        {
            await using var zip = await ZipArchive.CreateAsync(http.Response.Body, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            foreach (var submission in files)
            {
                await using var source = await storage.OpenReadStreamAsync(submission.StoragePath, ct);
                if (source == null)
                {
                    logger.LogWarning("Export skipped {Submission}: file {Path} not found", submission.Id, submission.StoragePath);
                    missing.Add($"{submission.Group?.Name}, opdracht {submission.AssignmentCode}: {Path.GetFileName(submission.StoragePath)}");
                    continue;
                }

                var entry = zip.CreateEntry(EntryName(submission, used), Compression(submission.ContentType));
                entry.LastWriteTime = submission.CreatedAt.ToGameTime();
                await using var target = await entry.OpenAsync(ct);
                await source.CopyToAsync(target, ct);
            }

            // Said in the download itself: otherwise a missing file is only noticed much later, if at all
            if (missing.Count > 0)
            {
                var list = zip.CreateEntry(MissingListName, CompressionLevel.Optimal);
                await using var writer = new StreamWriter(await list.OpenAsync(ct), new UTF8Encoding(true));
                await writer.WriteAsync("Deze bestanden staan niet meer op de schijf en zitten niet in de download:\r\n" + string.Join("\r\n", missing) + "\r\n");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The download has started, so its status can no longer change. Breaking the connection makes the browser
            // report a failed download, instead of saving a ZIP that silently stops halfway.
            logger.LogError(ex, "Export of the files failed halfway; the download was broken off");
            http.Abort();
        }
    }

    /// <summary>The list in the ZIP of files that could not be found.</summary>
    public const string MissingListName = "ONTBREKENDE BESTANDEN.txt";

    /// <summary>"{group}/{awesome|approved}/{stored file name}", with a number added if two files end up with the same name.</summary>
    private static string EntryName(Submission submission, HashSet<string> used)
    {
        var folder = SafeName(submission.Group?.Name ?? "Onbekende groep");
        var statusFolder = submission.Status == SubmissionStatus.Awesome ? "awesome" : "approved";
        var fileName = SafeName(Path.GetFileName(submission.StoragePath));
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        var entry = $"{folder}/{statusFolder}/{fileName}";
        for (var n = 2; !used.Add(entry); n++)
        {
            entry = $"{folder}/{statusFolder}/{stem} ({n}){extension}";
        }

        return entry;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':']).ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().Trim('.');
        return safe.Length == 0 ? "_" : safe;
    }

    // Photos, video and audio are compressed already; zipping them again only costs time
    private static CompressionLevel Compression(string contentType) =>
        contentType.StartsWith("image/") || contentType.StartsWith("video/") || contentType.StartsWith("audio/")
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;

    private static string Field(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        // Excel runs a cell that starts with = + - or @ as a formula; a quote in front keeps it text
        if (value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Today(TimeProvider time) =>
        time.GetUtcNow().ToGameTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
