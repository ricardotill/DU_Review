using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Services;

public class DatabaseInitializer
{
    private readonly ScoutingDbContext _context;
    private readonly IFileStorageService _fileStorage;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        ScoutingDbContext context,
        IFileStorageService fileStorage,
        IOptions<ScoutingSettings> options,
        ILogger<DatabaseInitializer> logger)
    {
        _context = context;
        _fileStorage = fileStorage;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// EnsureCreated does not change an existing database, so columns added later are added here.
    /// </summary>
    private async Task AddMissingColumnsAsync(CancellationToken ct)
    {
        if (!_context.Database.IsRelational()) return;

        if (_context.Database.IsSqlite())
        {
            await AddSqliteColumnAsync("Submissions", "SubmitterName", "TEXT NULL", ct);
            await AddSqliteColumnAsync("Submissions", "SlackFileId", "TEXT NULL", ct);
            await AddSqliteColumnAsync("Groups", "AdventurersPoints", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddSqliteColumnAsync("Groups", "AdventurersPointsUpdatedAt", "TEXT NULL", ct);
            await AddSqliteColumnAsync("Groups", "TravelAgencyPoints", "INTEGER NOT NULL DEFAULT 0", ct);
            await AddSqliteColumnAsync("Groups", "TravelAgencyPointsUpdatedAt", "TEXT NULL", ct);
        }
        else if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Submissions\" ADD COLUMN IF NOT EXISTS \"SubmitterName\" character varying(100) NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Submissions\" ADD COLUMN IF NOT EXISTS \"SlackFileId\" character varying(50) NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD COLUMN IF NOT EXISTS \"AdventurersPoints\" integer NOT NULL DEFAULT 0", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD COLUMN IF NOT EXISTS \"AdventurersPointsUpdatedAt\" timestamp with time zone NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD COLUMN IF NOT EXISTS \"TravelAgencyPoints\" integer NOT NULL DEFAULT 0", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD COLUMN IF NOT EXISTS \"TravelAgencyPointsUpdatedAt\" timestamp with time zone NULL", ct);
        }
        else
        {
            return;
        }

        // Same SQL for both databases; IF NOT EXISTS makes it safe to run at every start
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.UniqueFileIndexName}\" ON \"Submissions\" " +
            "(\"SlackChannelId\", \"SlackMessageId\", \"SlackFileId\") WHERE \"SlackFileId\" IS NOT NULL", ct);

        await AddFailedSlackEventsTableAsync(ct);
        await AddBonusPointsEntriesTableAsync(ct);
    }

    /// <summary>Added after the first release: a database created before it has no such table.</summary>
    private async Task AddFailedSlackEventsTableAsync(CancellationToken ct)
    {
        var sql = _context.Database.IsSqlite()
            ? """
              CREATE TABLE IF NOT EXISTS "FailedSlackEvents" (
                  "Id" TEXT NOT NULL CONSTRAINT "PK_FailedSlackEvents" PRIMARY KEY,
                  "EventKey" TEXT NOT NULL,
                  "Kind" TEXT NOT NULL,
                  "ChannelId" TEXT NULL,
                  "MessageTs" TEXT NULL,
                  "PayloadJson" TEXT NOT NULL,
                  "Error" TEXT NOT NULL,
                  "Attempts" INTEGER NOT NULL,
                  "Status" TEXT NOT NULL,
                  "CreatedAt" TEXT NOT NULL,
                  "UpdatedAt" TEXT NOT NULL)
              """
            : """
              CREATE TABLE IF NOT EXISTS "FailedSlackEvents" (
                  "Id" uuid NOT NULL CONSTRAINT "PK_FailedSlackEvents" PRIMARY KEY,
                  "EventKey" character varying(200) NOT NULL,
                  "Kind" character varying(30) NOT NULL,
                  "ChannelId" character varying(50) NULL,
                  "MessageTs" character varying(50) NULL,
                  "PayloadJson" text NOT NULL,
                  "Error" character varying(2000) NOT NULL,
                  "Attempts" integer NOT NULL,
                  "Status" character varying(20) NOT NULL,
                  "CreatedAt" timestamp with time zone NOT NULL,
                  "UpdatedAt" timestamp with time zone NOT NULL)
              """;
        await _context.Database.ExecuteSqlRawAsync(sql, ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.FailedEventKeyIndexName}\" ON \"FailedSlackEvents\" (\"EventKey\")", ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX IF NOT EXISTS \"{ScoutingDbContext.FailedEventStatusIndexName}\" ON \"FailedSlackEvents\" (\"Status\")", ct);
    }

    /// <summary>Added after the first release: a database created before it has no such table.</summary>
    private async Task AddBonusPointsEntriesTableAsync(CancellationToken ct)
    {
        var sql = _context.Database.IsSqlite()
            ? """
              CREATE TABLE IF NOT EXISTS "BonusPointsEntries" (
                  "Id" TEXT NOT NULL CONSTRAINT "PK_BonusPointsEntries" PRIMARY KEY,
                  "GroupId" TEXT NOT NULL,
                  "Category" TEXT NOT NULL,
                  "Delta" INTEGER NOT NULL,
                  "Total" INTEGER NOT NULL,
                  "ChangedAt" TEXT NOT NULL,
                  CONSTRAINT "FK_BonusPointsEntries_Groups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "Groups" ("Id") ON DELETE CASCADE)
              """
            : """
              CREATE TABLE IF NOT EXISTS "BonusPointsEntries" (
                  "Id" uuid NOT NULL CONSTRAINT "PK_BonusPointsEntries" PRIMARY KEY,
                  "GroupId" uuid NOT NULL,
                  "Category" character varying(20) NOT NULL,
                  "Delta" integer NOT NULL,
                  "Total" integer NOT NULL,
                  "ChangedAt" timestamp with time zone NOT NULL,
                  CONSTRAINT "FK_BonusPointsEntries_Groups_GroupId" FOREIGN KEY ("GroupId") REFERENCES "Groups" ("Id") ON DELETE CASCADE)
              """;
        await _context.Database.ExecuteSqlRawAsync(sql, ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE INDEX IF NOT EXISTS \"{ScoutingDbContext.BonusEntryGroupIndexName}\" ON \"BonusPointsEntries\" (\"GroupId\")", ct);
    }

    private async Task AddSqliteColumnAsync(string table, string column, string definition, CancellationToken ct)
    {
        var exists = await _context.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({0}) WHERE name = {1}", table, column)
            .SingleAsync(ct);
        if (exists == 0)
        {
            // Table and column names come from this class, never from input
#pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}", ct);
#pragma warning restore EF1002
            _logger.LogInformation("Added column {Column} to {Table}", column, table);
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting database initialization and synchronization...");

        try
        {
            await _context.Database.EnsureCreatedAsync(ct);
            await AddMissingColumnsAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database schema creation failed.");
            throw;
        }

        // Read before anything changes: a broken points file stops the start, with nothing half done
        var pointsFile = Path.GetFullPath(_options.Value.AssignmentPointsFile);
        IReadOnlyList<Assignment>? configuredAssignments = null;
        if (File.Exists(pointsFile))
        {
            using var reader = new StreamReader(pointsFile);
            try
            {
                configuredAssignments = AssignmentPointsCsvReader.Read(reader);
            }
            catch (FormatException ex)
            {
                throw new StartupConfigurationException($"Het puntenbestand {pointsFile} klopt niet: {ex.Message}", ex);
            }
        }

        // FR-003: Synchronize Groups from appsettings.json
        var configuredGroups = _options.Value.Groups;
        EnsureDistinctFolders(configuredGroups);
        if (configuredGroups != null && configuredGroups.Count > 0)
        {
            foreach (var cfg in configuredGroups)
            {
                var existing = await _context.Groups.FirstOrDefaultAsync(g => g.SlackChannelId == cfg.SlackChannelId, ct);
                if (existing == null)
                {
                    _context.Groups.Add(new Group
                    {
                        Id = Guid.NewGuid(),
                        Name = cfg.Name,
                        Abbreviation = cfg.Abbreviation,
                        SlackChannelId = cfg.SlackChannelId,
                        Description = cfg.Description
                    });
                }
                else
                {
                    existing.Name = cfg.Name;
                    existing.Abbreviation = cfg.Abbreviation;
                    existing.Description = cfg.Description;
                }

                await _fileStorage.EnsureFolderStructureAsync(cfg.Name, ct);
            }
            await _context.SaveChangesAsync(ct);

            // Kept, as submissions may point to them, but they get no new submissions
            var channels = configuredGroups.Select(g => g.SlackChannelId).ToHashSet();
            foreach (var old in await _context.Groups.Where(g => !channels.Contains(g.SlackChannelId)).ToListAsync(ct))
            {
                _logger.LogWarning("Group {Group} ({Channel}) is in the database but no longer in the settings", old.Name, old.SlackChannelId);
            }
        }

        // Synchronize Assignments and their points from the CSV file
        if (configuredAssignments != null)
        {
            var existingAssignments = await _context.Assignments.ToDictionaryAsync(a => a.Number, ct);
            foreach (var cfg in configuredAssignments)
            {
                if (existingAssignments.TryGetValue(cfg.Number, out var existing))
                {
                    existing.StandardPoints = cfg.StandardPoints;
                    existing.AwesomePoints = cfg.AwesomePoints;
                }
                else
                {
                    _context.Assignments.Add(cfg);
                }
            }

            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Synchronized {Count} assignments from {File}", configuredAssignments.Count, pointsFile);

            var numbers = configuredAssignments.Select(a => a.Number).ToHashSet();
            var removed = existingAssignments.Keys.Where(n => !numbers.Contains(n)).Order().ToList();
            if (removed.Count > 0)
            {
                // Still valid: submissions may already count for them. Delete them from the database by hand if they must go.
                _logger.LogWarning("Assignments {Numbers} are in the database but no longer in {File}; they still count", string.Join(", ", removed), pointsFile);
            }
        }
        else
        {
            _logger.LogWarning("Assignment points file not found at {File}; assignments not synchronized", pointsFile);
        }

        // Sample submissions are for trying the app out: only in simulation mode, never next to real Slack data
        if (_options.Value.Slack.SimulationMode && !await _context.Submissions.AnyAsync(ct))
        {
            var groups = await _context.Groups.ToListAsync(ct);
            if (groups.Count > 0)
            {
                _logger.LogInformation("Seeding initial demonstration submissions...");
                var sampleDefs = new[]
                {
                    (groupIndex: 0, assignNum: 1, user: "Ruben_Leiding", file: "kampvuur_zeearenden.jpg", status: SubmissionStatus.Approved, msg: "Opdracht 1: Ons kampvuur brandt perfect!", rev: "thumbsup", reviewer: "Sara_Jury"),
                    (groupIndex: 0, assignNum: 2, user: "Ruben_Leiding", file: "vlaggemast.png", status: SubmissionStatus.Awesome, msg: "#2 Vlaggenmast staat fier overeind! Met driepikkel.", rev: "heart", reviewer: "Mark_Jury"),
                    (groupIndex: 0, assignNum: 3, user: "Lars_Scout", file: "knopen_video.mp4", status: SubmissionStatus.Pending, msg: "Hierbij opdracht 3 de mastworp en paalsteek", rev: (string?)null, reviewer: (string?)null),
                    (groupIndex: 1, assignNum: 1, user: "Emma_Patrouille", file: "vuur_bosuilen.jpg", status: SubmissionStatus.Approved, msg: "Opdracht #1 vuurtje van patrouille De Bosuilen", rev: "thumbsup", reviewer: "Sara_Jury"),
                    (groupIndex: 1, assignNum: 4, user: "Daan_Scout", file: "gps_track.pdf", status: SubmissionStatus.Rejected, msg: "A4 Kompastocht voltooid!", rev: "x", reviewer: "Alex_Jury"),
                    (groupIndex: 1, assignNum: 7, user: "Emma_Patrouille", file: "scout_koken.jpg", status: SubmissionStatus.Pending, msg: "Opdracht 7: Lekker pannenkoeken bakken boven kolen", rev: (string?)null, reviewer: (string?)null),
                    (groupIndex: 2, assignNum: 2, user: "Thijs_Wolven", file: "pionier_toren.jpg", status: SubmissionStatus.Awesome, msg: "#2 Kijk onze megahoge toren met sjorringen!", rev: "star", reviewer: "Alex_Jury"),
                    (groupIndex: 2, assignNum: 5, user: "Sofie_Wolven", file: "houtsnijden.jpg", status: SubmissionStatus.Approved, msg: "Opdracht 5: Houten lepel klaar voor de maaltijd", rev: "thumbsup", reviewer: "Mark_Jury"),
                    (groupIndex: 3, assignNum: 6, user: "Lucas_Poolvos", file: "ehbo_verband.jpg", status: SubmissionStatus.Pending, msg: "Taak 6: Verbandje vakkundig aangelegd", rev: (string?)null, reviewer: (string?)null),
                };

                foreach (var sample in sampleDefs)
                {
                    var group = groups[sample.groupIndex % groups.Count];
                    using var ms = SlackClient.CreateSimulatedFileStream(sample.file);
                    var createdAt = DateTimeOffset.UtcNow.AddHours(-1 * (sample.assignNum * 2));
                    var storedName = SubmissionFileName.Build(group, sample.assignNum, sample.file, createdAt.ToGameTime());
                    var path = await _fileStorage.SaveInitialFileAsync(group.Name, storedName, ms, ct);

                    if (sample.status != SubmissionStatus.Pending)
                    {
                        path = await _fileStorage.MoveFileToStatusAsync(group.Name, path, sample.status, ct);
                    }

                    var sub = new Submission
                    {
                        Id = Guid.NewGuid(),
                        GroupId = group.Id,
                        AssignmentNumber = sample.assignNum,
                        SlackChannelId = group.SlackChannelId,
                        SlackMessageId = $"172000000{sample.assignNum}.000{sample.groupIndex}",
                        Submitter = sample.user,
                        FileName = sample.file,
                        StoragePath = path,
                        FileSizeBytes = 1024 * (sample.assignNum + 5),
                        ContentType = sample.file.EndsWith(".pdf") ? "application/pdf" : sample.file.EndsWith(".mp4") ? "video/mp4" : "image/jpeg",
                        Status = sample.status,
                        MessageText = sample.msg,
                        CreatedAt = createdAt
                    };

                    if (sample.reviewer != null)
                    {
                        sub.Reviews.Add(new Review
                        {
                            Id = Guid.NewGuid(),
                            SubmissionId = sub.Id,
                            Reviewer = sample.reviewer,
                            Status = sample.status,
                            ReactionEmoji = sample.rev,
                            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1 * (sample.assignNum * 2) + 1)
                        });
                    }

                    _context.Submissions.Add(sub);
                }

                await _context.SaveChangesAsync(ct);
                _logger.LogInformation("Finished seeding demo submissions");
            }
        }

        await ReportMissingFilesAsync(ct);
    }

    /// <summary>Two groups whose names give the same folder would mix their files: the start is stopped.</summary>
    private static void EnsureDistinctFolders(IReadOnlyCollection<GroupConfig>? groups)
    {
        var clash = (groups ?? Array.Empty<GroupConfig>())
            .GroupBy(g => LocalFileStorageService.FolderName(g.Name), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (clash != null)
        {
            throw new StartupConfigurationException(
                $"De groepen {string.Join(" en ", clash.Select(g => $"'{g.Name}'"))} krijgen dezelfde map '{clash.Key}'; geef ze een andere naam");
        }
    }

    /// <summary>
    /// Submissions whose file is not on disk: moved or deleted by hand, or left behind by a crash. They are
    /// only reported; the files can be put back, or the group rebuilt from Slack.
    /// </summary>
    private async Task ReportMissingFilesAsync(CancellationToken ct)
    {
        var paths = await _context.Submissions.Select(s => new { s.Id, s.StoragePath }).ToListAsync(ct);
        var missing = new List<string>();
        foreach (var submission in paths)
        {
            if (!await _fileStorage.FileExistsAsync(submission.StoragePath, ct)) missing.Add(submission.StoragePath);
        }

        if (missing.Count > 0)
        {
            _logger.LogWarning("{Count} of {Total} submissions have no file on disk, e.g. {Examples}",
                missing.Count, paths.Count, string.Join("; ", missing.Take(10)));
        }
    }
}
