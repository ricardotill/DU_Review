using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
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
            (await db.Assignments.SingleAsync(a => a.Number == 1)).StandardPoints.Should().Be(5);
            (await db.Assignments.CountAsync()).Should().Be(7, "assignments removed from the CSV are kept");
            (await db.Submissions.CountAsync()).Should().Be(before);
        }
    }

    [Fact]
    public async Task InitializeAsync_GroupsTableFromBeforeBonusPoints_ShouldAddColumnsAndKeepValuesOnRestart()
    {
        var path = Path.Combine(_temp.Path, "old.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={path}").Options;
        var settings = Settings();
        await using (var db = new ScoutingDbContext(options))
        {
            // A database from before the bonus columns existed
            await db.Database.EnsureCreatedAsync();
            foreach (var column in new[] { "AdventurersPoints", "AdventurersPointsUpdatedAt", "TravelAgencyPoints", "TravelAgencyPointsUpdatedAt" })
            {
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"Groups\" DROP COLUMN \"{column}\"");
#pragma warning restore EF1002
            }

            await Create(db, settings).InitializeAsync();
            var wolves = await db.Groups.SingleAsync(g => g.SlackChannelId == "C1");
            wolves.AdventurersPoints.Should().Be(0);
            wolves.AdventurersPoints = 12;
            wolves.TravelAgencyPoints = 7;
            await db.SaveChangesAsync();
        }

        await using (var db = new ScoutingDbContext(options))
        {
            await Create(db, settings).InitializeAsync();

            var wolves = await db.Groups.SingleAsync(g => g.SlackChannelId == "C1");
            wolves.AdventurersPoints.Should().Be(12, "the group sync only changes the names from the settings");
            wolves.TravelAgencyPoints.Should().Be(7);
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
            await new GroupRepository(db).SetBonusPointsAsync(new[] { new BonusPointsChange(wolves.Id, 0, 0, 5, 0) });
        }

        await using (var db = new ScoutingDbContext(options))
        {
            var entry = await db.BonusPointsEntries.SingleAsync();
            entry.Category.Should().Be(BonusCategory.Adventurers);
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
}
