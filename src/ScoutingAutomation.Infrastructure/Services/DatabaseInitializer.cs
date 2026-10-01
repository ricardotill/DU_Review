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
            await AddSqliteColumnAsync("SubmitterName", "TEXT NULL", ct);
            await AddSqliteColumnAsync("SlackFileId", "TEXT NULL", ct);
        }
        else if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Submissions\" ADD COLUMN IF NOT EXISTS \"SubmitterName\" character varying(100) NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Submissions\" ADD COLUMN IF NOT EXISTS \"SlackFileId\" character varying(50) NULL", ct);
        }
        else
        {
            return;
        }

        // Same SQL for both databases; IF NOT EXISTS makes it safe to run at every start
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.UniqueFileIndexName}\" ON \"Submissions\" " +
            "(\"SlackChannelId\", \"SlackMessageId\", \"SlackFileId\") WHERE \"SlackFileId\" IS NOT NULL", ct);
    }

    private async Task AddSqliteColumnAsync(string column, string definition, CancellationToken ct)
    {
        var exists = await _context.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Submissions') WHERE name = {0}", column)
            .SingleAsync(ct);
        if (exists == 0)
        {
            // Column names come from this class, never from input
#pragma warning disable EF1002
            await _context.Database.ExecuteSqlRawAsync($"ALTER TABLE \"Submissions\" ADD COLUMN \"{column}\" {definition}", ct);
#pragma warning restore EF1002
            _logger.LogInformation("Added column {Column} to Submissions", column);
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

        // FR-003: Synchronize Groups from appsettings.json
        var configuredGroups = _options.Value.Groups;
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
        }

        // Synchronize Assignments and their points from the CSV file
        var pointsFile = Path.GetFullPath(_options.Value.AssignmentPointsFile);
        if (File.Exists(pointsFile))
        {
            IReadOnlyList<Assignment> configuredAssignments;
            using (var reader = new StreamReader(pointsFile))
            {
                configuredAssignments = AssignmentPointsCsvReader.Read(reader);
            }

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
    }
}
