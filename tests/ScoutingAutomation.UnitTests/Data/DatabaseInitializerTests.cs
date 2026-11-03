using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Storage;
using Xunit;

namespace ScoutingAutomation.UnitTests.Data;

public class DatabaseInitializerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private ScoutingSettings Settings(string? csv = "Opdracht,standaard,awesome\n1,1,2\n2,2,4\n3,1,2\n4,1,2\n5,2,4\n6,1,2\n7,1,2\n") 
    {
        var csvPath = Path.Combine(_temp.Path, "punten.csv");
        if (csv != null) File.WriteAllText(csvPath, csv);
        return new ScoutingSettings
        {
            StorageRoot = Path.Combine(_temp.Path, "storage"),
            AssignmentPointsFile = csvPath,
            Groups = new List<GroupConfig>
            {
                new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1", Description = "Welpen" },
                new() { Name = "De Bosuilen", Abbreviation = "DBU", SlackChannelId = "C2" }
            },
            // Sample submissions are only made for trying the app out
            Slack = new SlackSettings { SimulationMode = true }
        };
    }

    private static DatabaseInitializer Create(ScoutingDbContext db, ScoutingSettings settings) =>
        new(db, new LocalFileStorageService(Options.Create(settings), NullLogger<LocalFileStorageService>.Instance),
            Options.Create(settings), NullLogger<DatabaseInitializer>.Instance);

    [Fact]
    public async Task InitializeAsync_EmptyDatabase_ShouldCreateGroupsAssignmentsFoldersAndSampleData()
    {
        await using var db = TestDb.Create();
        var settings = Settings();

        await Create(db, settings).InitializeAsync();

        (await db.Groups.CountAsync()).Should().Be(2);
        (await db.Assignments.CountAsync()).Should().Be(7);
        Directory.Exists(Path.Combine(settings.StorageRoot, "de-wolven", "approved")).Should().BeTrue();
        var samples = await db.Submissions.ToListAsync();
        samples.Should().NotBeEmpty();
        samples.Should().OnlyContain(s => File.Exists(s.StoragePath));
        samples.Where(s => s.GroupId == db.Groups.Single(g => g.Name == "De Wolven").Id)
            .Should().OnlyContain(s => Path.GetFileName(s.StoragePath).StartsWith("DWO "));
    }

    [Fact]
    public async Task InitializeAsync_SecondRun_ShouldUpdateGroupsAndPointsWithoutReseeding()
    {
        var name = $"Init_{Guid.NewGuid():N}";
        var settings = Settings();
        await using (var db = TestDb.Create(name))
        {
            await Create(db, settings).InitializeAsync();
        }

        settings.Groups[0].Abbreviation = "CDW";
        settings.Groups[0].Description = "Nieuw";
        File.WriteAllText(settings.AssignmentPointsFile, "Opdracht,standaard,awesome\n1,5,10\n");

        await using (var db = TestDb.Create(name))
        {
            var before = await db.Submissions.CountAsync();
            await Create(db, settings).InitializeAsync();

            var wolves = await db.Groups.SingleAsync(g => g.SlackChannelId == "C1");
            wolves.Abbreviation.Should().Be("CDW");
            wolves.Description.Should().Be("Nieuw");
            (await db.Assignments.SingleAsync(a => a.Code == "1")).StandardPoints.Should().Be(5);
            (await db.Assignments.CountAsync()).Should().Be(7, "assignments removed from the CSV are kept");
            (await db.Submissions.CountAsync()).Should().Be(before);
        }
    }

    [Fact]
    public async Task InitializeAsync_DatabaseWithFixedBonusColumns_ShouldMoveThemToCategories()
    {
        var path = Path.Combine(_temp.Path, "old.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings();
        var entered = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
        Guid wolvesId;
        await using (var db = new ScoutingDbContext(options))
        {
            await Create(db, settings).InitializeAsync();
            wolvesId = (await db.Groups.SingleAsync(g => g.SlackChannelId == "C1")).Id;

            // A database from the first game: two fixed columns on Groups, the history named after an enum
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE "GroupBonusPoints";
                ALTER TABLE "Groups" ADD COLUMN "AdventurersPoints" INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE "Groups" ADD COLUMN "AdventurersPointsUpdatedAt" TEXT NULL;
                ALTER TABLE "Groups" ADD COLUMN "TravelAgencyPoints" INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE "Groups" ADD COLUMN "TravelAgencyPointsUpdatedAt" TEXT NULL;
                """);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE \"Groups\" SET \"AdventurersPoints\" = 12, \"AdventurersPointsUpdatedAt\" = {0}, \"TravelAgencyPoints\" = 7 WHERE \"SlackChannelId\" = 'C1'",
                entered.ToString("yyyy-MM-dd HH:mm:ss+00:00"));
            db.BonusPointsEntries.AddRange(
                new BonusPointsEntry { GroupId = wolvesId, CategoryKey = "Adventurers", Delta = 12, Total = 12, ChangedAt = entered },
                new BonusPointsEntry { GroupId = wolvesId, CategoryKey = "TravelAgency", Delta = 7, Total = 7, ChangedAt = entered });
            await db.SaveChangesAsync();
        }

        for (var start = 0; start < 2; start++)
        {
            await using var db = new ScoutingDbContext(options);
            await Create(db, settings).InitializeAsync();
        }

        await using (var db = new ScoutingDbContext(options))
        {
            var points = await db.GroupBonusPoints.OrderBy(p => p.CategoryKey).ToListAsync();
            points.Select(p => (p.GroupId, p.CategoryKey, p.Points)).Should().Equal((wolvesId, "avonturiers", 12), (wolvesId, "reisbureau", 7));
            points[0].UpdatedAt.Should().Be(entered);
            (await db.BonusPointsEntries.Select(e => e.CategoryKey).OrderBy(k => k).ToListAsync()).Should().Equal("avonturiers", "reisbureau");

            var columns = await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM pragma_table_info('Groups')").ToListAsync();
            // The Avonturiers channel is a new column of its own, not one of the old points columns
            columns.Should().NotContain(c => (c.Contains("Adventurers") && c != "AdventurersSlackChannelId") || c.Contains("TravelAgency"));

            var stats = await new SubmissionRepository(db).GetStatisticsAsync();
            stats.GroupStatistics.Single(g => g.GroupId == wolvesId).BonusTotal.Should().Be(19);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData("Foto Wedstrijd", "Fotowedstrijd")]
    [InlineData("pubquiz", "")]
    public async Task InitializeAsync_InvalidBonusCategory_ShouldStopTheStart(string key, string name)
    {
        var settings = Settings();
        settings.BonusCategories = new List<BonusCategoryConfig> { new() { Key = key, Name = name } };
        await using var db = new ScoutingDbContext(new DbContextOptionsBuilder<ScoutingDbContext>().UseInMemoryDatabase($"Init_{Guid.NewGuid():N}").Options);

        var act = () => Create(db, settings).InitializeAsync();

        await act.Should().ThrowAsync<StartupConfigurationException>().WithMessage("*BonusCategories*");
    }

    [Fact]
    public async Task InitializeAsync_BonusCategoryKeyTwice_ShouldStopTheStart()
    {
        var settings = Settings();
        settings.BonusCategories = new List<BonusCategoryConfig> { new() { Key = "quiz", Name = "Quiz" }, new() { Key = "quiz", Name = "Quiz 2" } };
        await using var db = new ScoutingDbContext(new DbContextOptionsBuilder<ScoutingDbContext>().UseInMemoryDatabase($"Init_{Guid.NewGuid():N}").Options);

        var act = () => Create(db, settings).InitializeAsync();

        await act.Should().ThrowAsync<StartupConfigurationException>().WithMessage("*twee keer*");
    }

    [Fact]
    public async Task InitializeAsync_DatabaseWithAssignmentNumbers_ShouldMoveThemToCodes()
    {
        var path = Path.Combine(_temp.Path, "old.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings("Opdracht,standaard,awesome,titel,beschrijving\n1,1,2,Kampvuur,\n2,2,4\nA,3,6,Speciaal,\"Bouw, met z'n allen, een toren\"\n");
        var groupId = Guid.NewGuid();
        await using (var db = new ScoutingDbContext(options))
        {
            // A database from before assignment codes: numbers, with Assignments.Number NOT NULL and no default
            await db.Database.EnsureCreatedAsync();
            db.Groups.Add(new Domain.Entities.Group { Id = groupId, Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                DROP TABLE "Assignments";
                CREATE TABLE "Assignments" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Assignments" PRIMARY KEY,
                    "Number" INTEGER NOT NULL,
                    "StandardPoints" INTEGER NOT NULL,
                    "AwesomePoints" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL);
                CREATE UNIQUE INDEX "IX_Assignments_Number" ON "Assignments" ("Number");
                INSERT INTO "Assignments" VALUES ('7C9E6679-7425-40DE-944B-E07FC1F90AE7', 2, 1, 1, '2026-10-01 10:00:00+00:00');
                DROP INDEX "IX_Submissions_AssignmentCode";
                ALTER TABLE "Submissions" DROP COLUMN "AssignmentCode";
                ALTER TABLE "Submissions" ADD COLUMN "AssignmentNumber" INTEGER NULL;
                CREATE INDEX "IX_Submissions_AssignmentNumber" ON "Submissions" ("AssignmentNumber");
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Submissions" ("Id", "GroupId", "AssignmentNumber", "SlackChannelId", "SlackMessageId", "Submitter",
                    "FileName", "StoragePath", "FileSizeBytes", "ContentType", "Status", "CreatedAt")
                VALUES ('0F8FAD5B-D9CB-469F-A165-70867728950E', {0}, 2, 'C1', '1727700000.000100', 'U1',
                    'a.jpg', '/s/a.jpg', 1, 'image/jpeg', 'Approved', '2026-10-01 10:00:00+00:00')
                """, groupId.ToString().ToUpperInvariant());

            await Create(db, settings).InitializeAsync();
        }

        await using (var db = new ScoutingDbContext(options))
        {
            // A second start finds nothing left to move
            await Create(db, settings).InitializeAsync();

            var assignments = await new AssignmentRepository(db).GetAllAsync();
            assignments.Select(a => a.Code).Should().Equal("1", "2", "A");
            assignments[0].Title.Should().Be("Kampvuur");
            assignments[1].StandardPoints.Should().Be(2, "the points file wins over the old row");
            assignments[2].Description.Should().Be("Bouw, met z'n allen, een toren");
            (await db.Submissions.SingleAsync()).AssignmentCode.Should().Be("2");

            var columns = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS \"Value\" FROM pragma_table_info('Assignments') UNION ALL SELECT name FROM pragma_table_info('Submissions')").ToListAsync();
            columns.Should().NotContain(new[] { "Number", "AssignmentNumber" });
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeBonusHistory_ShouldAddTheTableOnce()
    {
        var path = Path.Combine(_temp.Path, "old.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings();
        await using (var db = new ScoutingDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"BonusPointsEntries\"");

            await Create(db, settings).InitializeAsync();
            await Create(db, settings).InitializeAsync();

            var wolves = await db.Groups.SingleAsync(g => g.SlackChannelId == "C1");
            await new GroupRepository(db).SetBonusPointsAsync(new[] { new BonusPointsChange(wolves.Id, "avonturiers", 0, 5) });
        }

        await using (var db = new ScoutingDbContext(options))
        {
            var entry = await db.BonusPointsEntries.SingleAsync();
            entry.CategoryKey.Should().Be("avonturiers");
            entry.Delta.Should().Be(5);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task InitializeAsync_MissingPointsFileAndNoGroups_ShouldStillStart()
    {
        await using var db = TestDb.Create();
        var settings = Settings(csv: null);
        settings.Groups = new List<GroupConfig>();

        await Create(db, settings).InitializeAsync();

        (await db.Assignments.AnyAsync()).Should().BeFalse();
        (await db.Submissions.AnyAsync()).Should().BeFalse("sample data needs groups");
    }

    [Fact]
    public async Task InitializeAsync_GroupsSectionLeftOut_ShouldSkipGroupSync()
    {
        await using var db = TestDb.Create();
        var settings = Settings();
        settings.Groups = null!;

        await Create(db, settings).InitializeAsync();

        (await db.Groups.AnyAsync()).Should().BeFalse();
        (await db.Assignments.CountAsync()).Should().Be(7);
    }

    [Fact]
    public async Task InitializeAsync_LiveMode_ShouldNotAddSampleData()
    {
        await using var db = TestDb.Create();
        var settings = Settings();
        settings.Slack.SimulationMode = false;

        await Create(db, settings).InitializeAsync();

        (await db.Groups.CountAsync()).Should().Be(2);
        (await db.Assignments.CountAsync()).Should().Be(7);
        (await db.Submissions.AnyAsync()).Should().BeFalse("sample data must never mix with real Slack data");
        Directory.GetFiles(settings.StorageRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_DatabaseUnavailable_ShouldThrow()
    {
        var unreachable = Path.Combine(_temp.Path, "missing-dir", "sub", "x.db");
        await using var db = new ScoutingDbContext(new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseSqlite($"Data Source={unreachable};Mode=ReadWrite").Options);

        var act = () => Create(db, Settings()).InitializeAsync();

        await act.Should().ThrowAsync<Exception>();
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeAvonturiers_ShouldAddKindChannelsAndLocations()
    {
        var path = Path.Combine(_temp.Path, "before-avonturiers.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings();
        settings.Groups[0].AdventurersSlackChannelId = "CAV1";
        var groupId = Guid.NewGuid();
        await using (var db = new ScoutingDbContext(options))
        {
            // A database from before the Avonturiers: no Kind column, no second channel or map token, no location tables
            await db.Database.EnsureCreatedAsync();
            db.Groups.Add(new Domain.Entities.Group { Id = groupId, Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("""
                DROP INDEX "IX_Submissions_Kind";
                ALTER TABLE "Submissions" DROP COLUMN "Kind";
                DROP INDEX "IX_Groups_EditionId_AdventurersSlackChannelId";
                DROP INDEX "IX_Groups_MapToken";
                ALTER TABLE "Groups" DROP COLUMN "AdventurersSlackChannelId";
                ALTER TABLE "Groups" DROP COLUMN "MapToken";
                DROP TABLE "LocationUnlocks";
                DROP TABLE "Locations";
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Submissions" ("Id", "GroupId", "AssignmentCode", "SlackChannelId", "SlackMessageId", "Submitter",
                    "FileName", "StoragePath", "FileSizeBytes", "ContentType", "Status", "CreatedAt")
                VALUES ('0F8FAD5B-D9CB-469F-A165-70867728950E', {0}, '2', 'C1', '1727700000.000100', 'U1',
                    'a.jpg', '/s/a.jpg', 1, 'image/jpeg', 'Approved', '2026-10-01 10:00:00+00:00')
                """, groupId.ToString().ToUpperInvariant());

            await Create(db, settings).InitializeAsync();
        }

        await using (var db = new ScoutingDbContext(options))
        {
            // A second start finds everything in place
            await Create(db, settings).InitializeAsync();

            var old = await db.Submissions.SingleAsync(s => s.SlackMessageId == "1727700000.000100");
            old.Kind.Should().Be(SubmissionKind.Assignment, "existing rows keep their meaning");
            var wolves = await db.Groups.SingleAsync(g => g.Id == groupId);
            wolves.AdventurersSlackChannelId.Should().Be("CAV1");
            wolves.MapToken.Should().NotBeNullOrWhiteSpace();
            (await db.Groups.Select(g => g.MapToken).Distinct().CountAsync()).Should().Be(2);

            db.Locations.Add(new Location { Number = 7, Name = "Molen", Latitude = 51.6, Longitude = 5.0 });
            db.LocationUnlocks.Add(new LocationUnlock { GroupId = groupId, LocationNumber = 7, UnlockedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            (await new LocationRepository(db).GetUnlocksAsync(groupId)).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task InitializeAsync_DatabaseFromBeforeEditions_ShouldMakeItTheFirstEdition()
    {
        var path = Path.Combine(_temp.Path, "before-editions.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings();
        var wolves = Guid.NewGuid().ToString().ToUpperInvariant();
        var owls = Guid.NewGuid().ToString().ToUpperInvariant();
        await using (var db = new ScoutingDbContext(options))
        {
            // The tables as they were before editions: no edition on groups, assignments and locations, no teams
            await db.Database.EnsureCreatedAsync();
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("""
                PRAGMA foreign_keys = OFF;
                DROP TABLE "Groups"; DROP TABLE "Assignments"; DROP TABLE "Locations"; DROP TABLE "Teams"; DROP TABLE "Editions";
                CREATE TABLE "Groups" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Groups" PRIMARY KEY,
                    "Name" TEXT NOT NULL, "Abbreviation" TEXT NULL, "SlackChannelId" TEXT NOT NULL,
                    "AdventurersSlackChannelId" TEXT NULL, "MapToken" TEXT NULL, "Description" TEXT NULL, "CreatedAt" TEXT NOT NULL);
                CREATE UNIQUE INDEX "IX_Groups_SlackChannelId" ON "Groups" ("SlackChannelId");
                CREATE UNIQUE INDEX "IX_Groups_AdventurersSlackChannelId" ON "Groups" ("AdventurersSlackChannelId") WHERE "AdventurersSlackChannelId" IS NOT NULL;
                CREATE UNIQUE INDEX "IX_Groups_MapToken" ON "Groups" ("MapToken") WHERE "MapToken" IS NOT NULL;
                CREATE TABLE "Assignments" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_Assignments" PRIMARY KEY,
                    "Code" TEXT NOT NULL, "Title" TEXT NULL, "Description" TEXT NULL,
                    "StandardPoints" INTEGER NOT NULL, "AwesomePoints" INTEGER NOT NULL, "CreatedAt" TEXT NOT NULL);
                CREATE UNIQUE INDEX "IX_Assignments_Code" ON "Assignments" ("Code");
                CREATE TABLE "Locations" (
                    "Number" INTEGER NOT NULL CONSTRAINT "PK_Locations" PRIMARY KEY,
                    "Name" TEXT NOT NULL, "Latitude" REAL NOT NULL, "Longitude" REAL NOT NULL, "Hint" TEXT NULL);
                PRAGMA foreign_keys = ON;
                """);
            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO "Groups" ("Id", "Name", "Abbreviation", "SlackChannelId", "MapToken", "CreatedAt")
                    VALUES ({0}, 'De Wolven', 'DWO', 'C1', 'token1', '2025-09-01 10:00:00+00:00'),
                           ({1}, 'Oud team', NULL, 'C9', 'token9', '2025-09-01 10:00:00+00:00');
                INSERT INTO "Assignments" ("Id", "Code", "StandardPoints", "AwesomePoints", "CreatedAt")
                    VALUES ('7C9E6679-7425-40DE-944B-E07FC1F90AE7', '2', 1, 1, '2025-09-01 10:00:00+00:00');
                INSERT INTO "Locations" VALUES (7, 'Molen', 51.6, 5.0, NULL);
                INSERT INTO "LocationUnlocks" VALUES ({0}, 7, '2025-10-04 10:00:00+00:00');
                INSERT INTO "Submissions" ("Id", "GroupId", "AssignmentCode", "SlackChannelId", "SlackMessageId", "Submitter",
                    "FileName", "StoragePath", "FileSizeBytes", "ContentType", "Status", "CreatedAt")
                VALUES ('0F8FAD5B-D9CB-469F-A165-70867728950E', {0}, '2', 'C1', '1727700000.000100', 'U1',
                    'a.jpg', '/s/a.jpg', 1, 'image/jpeg', 'Approved', '2025-10-04 10:00:00+00:00')
                """, wolves, owls);
            await db.Database.CloseConnectionAsync();

            await Create(db, settings).InitializeAsync();
        }

        Directory.GetFiles(Path.Combine(settings.StorageRoot, "_backup"), "pre-editions-*.db").Should().ContainSingle();

        await using (var db = new ScoutingDbContext(options))
        {
            // A second start finds nothing left to convert and makes no second copy
            await Create(db, settings).InitializeAsync();

            var edition = await db.Editions.SingleAsync();
            edition.Should().BeEquivalentTo(new { Name = "DU 2025", Year = 2025, Status = EditionStatus.Active }, "the game is from the year of its first submission");
            var groups = await db.Groups.ToListAsync();
            groups.Should().HaveCount(3, "both old groups are kept and De Bosuilen is added");
            groups.Should().OnlyContain(g => g.EditionId == edition.Id && g.TeamId != Guid.Empty);
            groups.Single(g => g.Name == "De Wolven").TeamId.Should().Be(Guid.Parse(wolves), "group accounts keep pointing at the group's id");
            (await db.Teams.Select(t => t.Name).ToListAsync()).Should().BeEquivalentTo("De Wolven", "Oud team", "De Bosuilen");
            (await db.Assignments.Select(a => a.Code).ToListAsync()).Should().Contain("2");
            (await db.Locations.SingleAsync()).Name.Should().Be("Molen");
            (await new LocationRepository(db).GetUnlocksAsync(Guid.Parse(wolves))).Should().ContainSingle();
            (await db.Submissions.SingleAsync()).GroupId.Should().Be(Guid.Parse(wolves));

            var indexes = await db.Database.SqlQueryRaw<string>(
                "SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' AND tbl_name IN ('Groups', 'Assignments', 'Locations', 'Editions')").ToListAsync();
            indexes.Should().BeEquivalentTo(await FreshIndexesAsync());
        }

        Directory.GetFiles(Path.Combine(settings.StorageRoot, "_backup"), "pre-editions-*.db").Should().ContainSingle();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private async Task<List<string>> FreshIndexesAsync()
    {
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={Path.Combine(_temp.Path, "fresh.db")}").Options;
        await using var db = new ScoutingDbContext(options);
        await db.Database.EnsureCreatedAsync();
        return await db.Database.SqlQueryRaw<string>(
            "SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%' AND tbl_name IN ('Groups', 'Assignments', 'Locations', 'Editions')").ToListAsync();
    }

    [Fact]
    public async Task InitializeAsync_EmptyDatabase_ShouldMakeTheActiveEditionOfThisYear()
    {
        await using var db = TestDb.Create();

        await Create(db, Settings()).InitializeAsync();

        var edition = await db.Editions.SingleAsync();
        edition.Status.Should().Be(EditionStatus.Active);
        edition.Year.Should().Be(DateTimeOffset.UtcNow.Year);
        (await db.Groups.ToListAsync()).Should().OnlyContain(g => g.EditionId == edition.Id);
    }

    [Fact]
    public async Task InitializeAsync_ChannelUsedTwice_ShouldStop()
    {
        var settings = Settings();
        settings.Groups[1].AdventurersSlackChannelId = "C1";
        await using var db = TestDb.Create();

        var act = () => Create(db, settings).InitializeAsync();

        await act.Should().ThrowAsync<StartupConfigurationException>();
    }
}
