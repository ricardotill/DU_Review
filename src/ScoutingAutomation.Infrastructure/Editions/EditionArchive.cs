using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Storage;

namespace ScoutingAutomation.Infrastructure.Editions;

/// <summary>
/// An edition as one ZIP: "edition.json" with everything in the database about it (groups and their teams,
/// assignments, locations, submissions with their reviews, points entered by hand, unlocks) and the submitted files
/// under "files/". Imported, it becomes a new edition next to the others: archived, or still in preparation if it was.
/// </summary>
public sealed class EditionArchive
{
    public const string Format = "du-review-edition";
    public const int Version = 1;
    public const string DataEntry = "edition.json";
    public const string FilesFolder = "files/";

    /// <summary>The most an import may unpack: a ZIP that claims more is refused before anything is written.</summary>
    public const long MaxUnpackedBytes = 50L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ScoutingDbContext _db;
    private readonly ISubmissionRepository _submissions;
    private readonly IFileStorageService _storage;
    private readonly string _storageRoot;
    private readonly TimeProvider _time;
    private readonly ILogger<EditionArchive> _logger;

    public EditionArchive(
        ScoutingDbContext db,
        ISubmissionRepository submissions,
        IFileStorageService storage,
        IOptions<ScoutingSettings> options,
        TimeProvider time,
        ILogger<EditionArchive> logger)
    {
        _db = db;
        _submissions = submissions;
        _storage = storage;
        _storageRoot = Path.GetFullPath(string.IsNullOrWhiteSpace(options.Value.StorageRoot) ? "./scouting_storage" : options.Value.StorageRoot);
        _time = time;
        _logger = logger;
    }

    /// <summary>The file name a download of this edition gets.</summary>
    public static string FileName(Edition edition) => $"{LocalFileStorageService.FolderName(edition.Name)}.du-editie.zip";

    public async Task<Edition?> FindAsync(Guid editionId, CancellationToken ct = default) =>
        await _db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == editionId, ct);

    // ---------------------------------------------------------------- export

    /// <summary>Writes the edition as a ZIP to <paramref name="output"/>, file by file, so large videos never sit in memory.</summary>
    /// <returns>The files that were in the database but no longer on disk; they are listed, not in the ZIP.</returns>
    public async Task<IReadOnlyList<string>> ExportAsync(Guid editionId, Stream output, CancellationToken ct = default)
    {
        // The rows and the standing are read under the edition lock, so an edition started meanwhile cannot mix in;
        // the files are streamed outside it, so a long download does not hold up other edition changes
        var (edition, submissions, files, data) = await EditionService.OneAtATimeAsync(() => ReadForExportAsync(editionId, ct), ct);

        var missing = new List<string>();
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        var dataEntry = zip.CreateEntry(DataEntry, CompressionLevel.Optimal);
        await using (var stream = await dataEntry.OpenAsync(ct))
        {
            await JsonSerializer.SerializeAsync(stream, data, Json, ct);
        }

        foreach (var submission in submissions)
        {
            await using var source = await _storage.OpenReadStreamAsync(submission.StoragePath, ct);
            if (source == null)
            {
                missing.Add(files[submission.Id]);
                continue;
            }

            var entry = zip.CreateEntry(files[submission.Id], Compression(submission.ContentType));
            await using var target = await entry.OpenAsync(ct);
            await source.CopyToAsync(target, ct);
        }

        if (missing.Count > 0)
        {
            _logger.LogWarning("Export of edition {Edition}: {Count} files are no longer on disk", edition.Name, missing.Count);
        }

        _logger.LogInformation("Exported edition {Edition}: {Groups} groups, {Submissions} submissions", edition.Name, data.Groups.Count, submissions.Count);
        return missing;
    }

    private async Task<(Edition Edition, List<Submission> Submissions, Dictionary<Guid, string> Files, ArchiveData Data)> ReadForExportAsync(
        Guid editionId, CancellationToken ct)
    {
        var edition = await FindAsync(editionId, ct) ?? throw new InvalidOperationException("Edition not found");
        var groups = await _db.Groups.IgnoreQueryFilters().AsNoTracking().Include(g => g.Team)
            .Where(g => g.EditionId == editionId).OrderBy(g => g.Name).ToListAsync(ct);
        var groupIds = groups.Select(g => g.Id).ToList();
        var submissions = await _db.Submissions.IgnoreQueryFilters().AsNoTracking().Include(s => s.Reviews)
            .Where(s => groupIds.Contains(s.GroupId)).ToListAsync(ct);
        // SQLite cannot order by DateTimeOffset in SQL
        submissions = submissions.OrderBy(s => s.CreatedAt).ToList();

        // The game being played has no final standing yet: the standing of now goes along
        var standing = edition.Status == EditionStatus.Active
            ? JsonSerializer.Serialize((await _submissions.GetStatisticsAsync(ct)).GroupStatistics.OrderBy(g => g.Rank)
                .Select(g => new EditionStanding(groups.FirstOrDefault(x => x.Id == g.GroupId)?.TeamId ?? Guid.Empty, g.GroupId, g.GroupName, g.Rank, g.Score)))
            : edition.FinalStandingJson;

        var editionRoot = EditionRoot(edition.StorageFolder);
        var groupNames = groups.ToDictionary(g => g.Id, g => g.Name);
        var files = submissions.ToDictionary(s => s.Id, s => FilesFolder + RelativeFilePath(editionRoot, s, groupNames));

        var data = new ArchiveData(
            Format, Version, _time.GetUtcNow(),
            new EditionData(edition.Id, edition.Name, edition.Year, edition.Status, edition.CreatedAt, edition.ArchivedAt, standing),
            groups.Select(g => g.Team).OfType<Team>().DistinctBy(t => t.Id).Select(t => new TeamData(t.Id, t.Name, t.CreatedAt)).ToList(),
            groups.Select(g => new GroupData(g.Id, g.TeamId, g.Name, g.Abbreviation, g.SlackChannelId, g.AdventurersSlackChannelId, g.MapToken, g.Description, g.CreatedAt)).ToList(),
            await _db.Assignments.IgnoreQueryFilters().AsNoTracking().Where(a => a.EditionId == editionId)
                .Select(a => new AssignmentData(a.Code, a.Title, a.Description, a.StandardPoints, a.AwesomePoints, a.CreatedAt)).ToListAsync(ct),
            await _db.Locations.IgnoreQueryFilters().AsNoTracking().Where(l => l.EditionId == editionId).OrderBy(l => l.Number)
                .Select(l => new LocationData(l.Number, l.Name, l.Latitude, l.Longitude, l.Hint)).ToListAsync(ct),
            submissions.Select(s => new SubmissionData(s.Id, s.GroupId, s.AssignmentCode, s.Kind, s.SlackChannelId, s.SlackMessageId, s.SlackFileId,
                s.Submitter, s.SubmitterName, s.FileName, files[s.Id], s.FileSizeBytes, s.ContentType, s.Status, s.MessageText, s.UnresolvedReason,
                s.CreatedAt, s.UpdatedAt,
                s.Reviews.OrderBy(r => r.CreatedAt).Select(r => new ReviewData(r.Reviewer, r.Status, r.ReactionEmoji, r.Notes, r.CreatedAt)).ToList())).ToList(),
            await _db.GroupBonusPoints.IgnoreQueryFilters().AsNoTracking().Where(b => groupIds.Contains(b.GroupId))
                .Select(b => new BonusPointsData(b.GroupId, b.CategoryKey, b.Points, b.UpdatedAt)).ToListAsync(ct),
            await _db.BonusPointsEntries.IgnoreQueryFilters().AsNoTracking().Where(b => groupIds.Contains(b.GroupId))
                .Select(b => new BonusEntryData(b.GroupId, b.CategoryKey, b.Delta, b.Total, b.ChangedAt)).ToListAsync(ct),
            await _db.LocationUnlocks.IgnoreQueryFilters().AsNoTracking().Where(u => groupIds.Contains(u.GroupId))
                .Select(u => new UnlockData(u.GroupId, u.LocationNumber, u.UnlockedAt)).ToListAsync(ct));

        return (edition, submissions, files, data);
    }

    private string EditionRoot(string? storageFolder) =>
        string.IsNullOrEmpty(storageFolder) ? _storageRoot : Path.GetFullPath(Path.Combine(_storageRoot, storageFolder));

    /// <summary>
    /// Where the file is inside its edition's folder ("de-wolven/approved/DWO 5 ….jpg"), so an import puts it in the
    /// same place; "{group}/{file}" for a file somewhere else. Never ".." or a full path.
    /// </summary>
    private static string RelativeFilePath(string editionRoot, Submission submission, IReadOnlyDictionary<Guid, string> groupNames)
    {
        var relative = Path.GetRelativePath(editionRoot, submission.StoragePath);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or "."))
        {
            relative = Path.Combine(LocalFileStorageService.FolderName(groupNames.GetValueOrDefault(submission.GroupId) ?? "groep"),
                Path.GetFileName(submission.StoragePath));
        }

        // Two submissions of one message can share a file only by accident; the id keeps every entry its own
        var directory = Path.GetDirectoryName(relative) ?? "";
        var name = Path.GetFileName(relative);
        return Path.Combine(directory, $"{submission.Id:N}-{name}").Replace('\\', '/');
    }

    private static CompressionLevel Compression(string contentType) =>
        contentType.StartsWith("image/") || contentType.StartsWith("video/") || contentType.StartsWith("audio/")
            ? CompressionLevel.NoCompression
            : CompressionLevel.Optimal;

    // ---------------------------------------------------------------- import

    /// <summary>
    /// Adds the edition in the ZIP as a new edition: never the active one, so the game being played does not change.
    /// Teams that are already here (same id, e.g. an export of this app) are used again, so group accounts see it.
    /// </summary>
    /// <returns>The new edition, or why the file could not be imported; nothing is changed then.</returns>
    public Task<(Edition? Edition, string? Error)> ImportAsync(string zipPath, CancellationToken ct = default) =>
        EditionService.OneAtATimeAsync(() => ImportCoreAsync(zipPath, ct), ct);

    private async Task<(Edition? Edition, string? Error)> ImportCoreAsync(string zipPath, CancellationToken ct)
    {
        ZipArchive zip;
        try
        {
            zip = await ZipFile.OpenReadAsync(zipPath, ct);
        }
        catch (InvalidDataException)
        {
            return (null, "Dit is geen ZIP-bestand.");
        }

        await using (zip)
        {
            var (data, problem) = await ReadDataAsync(zip, ct);
            if (data == null) return (null, problem);

            var editions = await _db.Editions.AsNoTracking().ToListAsync(ct);
            if (editions.Any(e => string.Equals(e.Name, data.Edition.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return (null, $"Er is al een editie {data.Edition.Name}. Hernoem of verwijder die eerst.");
            }

            if (zip.Entries.Sum(e => e.Length) > MaxUnpackedBytes) return (null, "Het bestand is uitgepakt te groot.");

            var storageFolder = EditionService.NewStorageFolder(editions, data.Edition.Name);
            var root = Path.GetFullPath(Path.Combine(_storageRoot, storageFolder));
            for (var i = 2; Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any(); i++)
            {
                storageFolder = $"{EditionService.NewStorageFolder(editions, data.Edition.Name)}-x{i}";
                root = Path.GetFullPath(Path.Combine(_storageRoot, storageFolder));
            }

            try
            {
                var edition = await AddRowsAsync(data, storageFolder, root, zip, ct);
                _logger.LogInformation("Imported edition {Edition}: {Groups} groups, {Submissions} submissions", edition.Name, data.Groups.Count, data.Submissions.Count);
                return (edition, null);
            }
            catch (OperationCanceledException)
            {
                // The upload was broken off: leave nothing behind either
                _db.ChangeTracker.Clear();
                TryDelete(root);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import of edition {Edition} failed; its files are removed again", data.Edition.Name);
                _db.ChangeTracker.Clear();
                TryDelete(root);
                return (null, ex is ArchiveException ? ex.Message : "Importeren is mislukt; er is niets veranderd. Zie het logboek.");
            }
        }
    }

    private static async Task<(ArchiveData? Data, string? Problem)> ReadDataAsync(ZipArchive zip, CancellationToken ct)
    {
        var entry = zip.GetEntry(DataEntry);
        if (entry == null) return (null, $"Dit is geen editie van DU Review: {DataEntry} ontbreekt.");
        if (entry.Length > 512L * 1024 * 1024) return (null, $"{DataEntry} is te groot.");

        ArchiveData? data;
        try
        {
            await using var stream = await entry.OpenAsync(ct);
            data = await JsonSerializer.DeserializeAsync<ArchiveData>(stream, Json, ct);
        }
        catch (JsonException ex)
        {
            return (null, $"{DataEntry} klopt niet: {ex.Message}");
        }

        if (data?.Format != Format || data.Edition == null) return (null, "Dit is geen editie van DU Review.");
        // Lists left out of the file are empty
        data = data with
        {
            Teams = data.Teams ?? [], Groups = data.Groups ?? [], Assignments = data.Assignments ?? [], Locations = data.Locations ?? [],
            Submissions = (data.Submissions ?? []).Select(x => x with { Reviews = x.Reviews ?? [] }).ToList(),
            BonusPoints = data.BonusPoints ?? [], BonusEntries = data.BonusEntries ?? [], Unlocks = data.Unlocks ?? []
        };
        if (data.Version > Version) return (null, "Deze editie komt uit een nieuwere versie van DU Review. Werk de app eerst bij.");

        var name = data.Edition.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > Edition.MaxNameLength) return (null, "De editie in het bestand heeft geen geldige naam.");
        if (data.Groups.Any(g => string.IsNullOrWhiteSpace(g.Name) || string.IsNullOrWhiteSpace(g.SlackChannelId)))
        {
            return (null, "Een groep in het bestand heeft geen naam of kanaal.");
        }

        // Same rules as editing groups by hand: a channel belongs to one group (either kind), a team to one group per edition
        var channels = data.Groups.SelectMany(g => new[] { g.SlackChannelId, g.AdventurersSlackChannelId })
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
        if (channels.Distinct(StringComparer.Ordinal).Count() != channels.Count) return (null, "Een Slack-kanaal hoort bij meer dan één groep in het bestand.");
        if (data.Groups.Where(g => g.TeamId != Guid.Empty).GroupBy(g => g.TeamId).Any(t => t.Count() > 1)) return (null, "Een team staat twee keer in het bestand.");

        var groupIds = data.Groups.Select(g => g.Id).ToHashSet();
        if (groupIds.Count != data.Groups.Count) return (null, "Een groep staat twee keer in het bestand.");
        if (data.Submissions.Any(s => !groupIds.Contains(s.GroupId)) || data.BonusPoints.Any(b => !groupIds.Contains(b.GroupId))
            || data.BonusEntries.Any(b => !groupIds.Contains(b.GroupId)) || data.Unlocks.Any(u => !groupIds.Contains(u.GroupId)))
        {
            return (null, "Het bestand noemt een groep die er niet in staat.");
        }

        return (data with { Edition = data.Edition with { Name = name } }, null);
    }

    private async Task<Edition> AddRowsAsync(ArchiveData data, string storageFolder, string root, ZipArchive zip, CancellationToken ct)
    {
        var edition = new Edition
        {
            Name = data.Edition.Name,
            Year = data.Edition.Year,
            Status = data.Edition.Status == EditionStatus.Draft ? EditionStatus.Draft : EditionStatus.Archived,
            CreatedAt = data.Edition.CreatedAt,
            ArchivedAt = data.Edition.Status == EditionStatus.Draft ? null : data.Edition.ArchivedAt ?? data.ExportedAt,
            StorageFolder = storageFolder
        };
        _db.Editions.Add(edition);

        // A team that is here already (an export of this app) stays the same team; others are added with their id
        var existingTeams = await _db.Teams.Select(t => t.Id).ToListAsync(ct);
        foreach (var team in data.Teams.Where(t => !existingTeams.Contains(t.Id)).DistinctBy(t => t.Id))
        {
            _db.Teams.Add(new Team { Id = team.Id, Name = team.Name, CreatedAt = team.CreatedAt });
        }

        var knownTeams = existingTeams.Concat(data.Teams.Select(t => t.Id)).ToHashSet();
        var groupIds = new Dictionary<Guid, Guid>();
        foreach (var g in data.Groups)
        {
            var group = new Group
            {
                EditionId = edition.Id,
                // A group without a known team gets its own one
                TeamId = knownTeams.Contains(g.TeamId) ? g.TeamId : Guid.Empty,
                Name = g.Name.Trim(), Abbreviation = g.Abbreviation, SlackChannelId = g.SlackChannelId.Trim(),
                AdventurersSlackChannelId = g.AdventurersSlackChannelId, MapToken = DatabaseInitializer.NewMapToken(),
                Description = g.Description, CreatedAt = g.CreatedAt
            };
            _db.Groups.Add(group);
            groupIds[g.Id] = group.Id;
        }

        foreach (var a in data.Assignments.DistinctBy(a => a.Code))
        {
            _db.Assignments.Add(new Assignment
            {
                EditionId = edition.Id, Code = a.Code, Title = a.Title, Description = a.Description,
                StandardPoints = a.StandardPoints, AwesomePoints = a.AwesomePoints, CreatedAt = a.CreatedAt
            });
        }

        foreach (var l in data.Locations.DistinctBy(l => l.Number))
        {
            _db.Locations.Add(new Location { EditionId = edition.Id, Number = l.Number, Name = l.Name, Latitude = l.Latitude, Longitude = l.Longitude, Hint = l.Hint });
        }

        var rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        foreach (var s in data.Submissions)
        {
            var target = await UnpackAsync(zip, s.File, root, rootWithSeparator, ct);
            var submission = new Submission
            {
                GroupId = groupIds[s.GroupId], AssignmentCode = s.AssignmentCode, Kind = s.Kind, SlackChannelId = s.SlackChannelId,
                SlackMessageId = s.SlackMessageId, SlackFileId = s.SlackFileId, Submitter = s.Submitter, SubmitterName = s.SubmitterName,
                FileName = s.FileName, StoragePath = target, FileSizeBytes = s.FileSizeBytes, ContentType = s.ContentType,
                Status = s.Status, MessageText = s.MessageText, UnresolvedReason = s.UnresolvedReason, CreatedAt = s.CreatedAt, UpdatedAt = s.UpdatedAt
            };
            foreach (var r in s.Reviews)
            {
                submission.Reviews.Add(new Review
                {
                    Id = Guid.NewGuid(), SubmissionId = submission.Id, Reviewer = r.Reviewer, Status = r.Status,
                    ReactionEmoji = r.ReactionEmoji, Notes = r.Notes, CreatedAt = r.CreatedAt
                });
            }

            _db.Submissions.Add(submission);
        }

        foreach (var b in data.BonusPoints.DistinctBy(b => (b.GroupId, b.CategoryKey)))
        {
            _db.GroupBonusPoints.Add(new GroupBonusPoints { GroupId = groupIds[b.GroupId], CategoryKey = b.CategoryKey, Points = b.Points, UpdatedAt = b.UpdatedAt });
        }

        foreach (var b in data.BonusEntries)
        {
            _db.BonusPointsEntries.Add(new BonusPointsEntry { GroupId = groupIds[b.GroupId], CategoryKey = b.CategoryKey, Delta = b.Delta, Total = b.Total, ChangedAt = b.ChangedAt });
        }

        foreach (var u in data.Unlocks.DistinctBy(u => (u.GroupId, u.LocationNumber)))
        {
            _db.LocationUnlocks.Add(new LocationUnlock { GroupId = groupIds[u.GroupId], LocationNumber = u.LocationNumber, UnlockedAt = u.UnlockedAt });
        }

        // The standing points at this app's group ids
        edition.FinalStandingJson = RemapStanding(data.Edition.FinalStandingJson, groupIds);

        // Everything is unpacked: the save is not cancelled any more, so a broken-off upload can never remove the files
        // of rows that were committed after all
        await _db.SaveChangesAsync(CancellationToken.None);
        return edition;
    }

    /// <summary>Unpacks a submission's file to its place in the edition's folder; a file missing from the ZIP stays missing.</summary>
    private static async Task<string> UnpackAsync(ZipArchive zip, string? file, string root, string rootWithSeparator, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file) || !file.StartsWith(FilesFolder, StringComparison.Ordinal))
        {
            throw new ArchiveException("Een inzending in het bestand heeft geen geldige bestandsnaam.");
        }

        // The path comes from the file: it must stay inside the edition's folder
        var target = Path.GetFullPath(Path.Combine(root, file[FilesFolder.Length..]));
        if (!target.StartsWith(rootWithSeparator, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
        {
            throw new ArchiveException("Het bestand bevat een pad buiten de map van de editie.");
        }

        var entry = zip.GetEntry(file);
        if (entry == null) return target;

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var source = await entry.OpenAsync(ct);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(output, ct);
        return target;
    }

    private static string? RemapStanding(string? json, IReadOnlyDictionary<Guid, Guid> groupIds)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var standing = EditionService.ReadStanding(new Edition { FinalStandingJson = json });
        return JsonSerializer.Serialize(standing.Select(s => s with { GroupId = groupIds.GetValueOrDefault(s.GroupId, s.GroupId) }));
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove {Folder} after a failed import", folder);
        }
    }

    private sealed class ArchiveException(string message) : Exception(message);

    // ---------------------------------------------------------------- the file format (version 1)

    public sealed record ArchiveData(
        string Format, int Version, DateTimeOffset ExportedAt, EditionData Edition,
        List<TeamData> Teams, List<GroupData> Groups, List<AssignmentData> Assignments, List<LocationData> Locations,
        List<SubmissionData> Submissions, List<BonusPointsData> BonusPoints, List<BonusEntryData> BonusEntries, List<UnlockData> Unlocks);

    public sealed record EditionData(Guid Id, string Name, int Year, EditionStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? ArchivedAt, string? FinalStandingJson);

    public sealed record TeamData(Guid Id, string Name, DateTimeOffset CreatedAt);

    public sealed record GroupData(Guid Id, Guid TeamId, string Name, string? Abbreviation, string SlackChannelId, string? AdventurersSlackChannelId,
        string? MapToken, string? Description, DateTimeOffset CreatedAt);

    public sealed record AssignmentData(string Code, string? Title, string? Description, int StandardPoints, int AwesomePoints, DateTimeOffset CreatedAt);

    public sealed record LocationData(int Number, string Name, double Latitude, double Longitude, string? Hint);

    public sealed record SubmissionData(Guid Id, Guid GroupId, string? AssignmentCode, SubmissionKind Kind, string SlackChannelId, string SlackMessageId,
        string? SlackFileId, string Submitter, string? SubmitterName, string FileName, string File, long FileSizeBytes, string ContentType,
        SubmissionStatus Status, string? MessageText, string? UnresolvedReason, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, List<ReviewData> Reviews);

    public sealed record ReviewData(string Reviewer, SubmissionStatus Status, string? ReactionEmoji, string? Notes, DateTimeOffset CreatedAt);

    public sealed record BonusPointsData(Guid GroupId, string CategoryKey, int Points, DateTimeOffset? UpdatedAt);

    public sealed record BonusEntryData(Guid GroupId, string CategoryKey, int Delta, int Total, DateTimeOffset ChangedAt);

    public sealed record UnlockData(Guid GroupId, int LocationNumber, DateTimeOffset UnlockedAt);
}
