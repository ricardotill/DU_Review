using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Model;

/// <summary>Points entered by hand per group, in categories such as Avonturiers and Reisbureau.</summary>
public class BonusPointsTests
{
    private const string Av = "avonturiers";
    private const string Rb = "reisbureau";

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static ScoutingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ScoutingDbContext>().UseInMemoryDatabase($"Bonus_{Guid.NewGuid():N}").Options);

    private static GroupBonusPoints Points(Group group, string key, int points, DateTimeOffset? at = null) =>
        new() { GroupId = group.Id, CategoryKey = key, Points = points, UpdatedAt = at };

    private static async Task<GroupBonusPoints?> StoredAsync(ScoutingDbContext context, Group group, string key) =>
        await context.GroupBonusPoints.AsNoTracking().SingleOrDefaultAsync(p => p.GroupId == group.Id && p.CategoryKey == key);

    [Fact]
    public async Task GetStatisticsAsync_ShouldAddBonusToScoreAndRank()
    {
        await using var context = NewContext();
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2" };
        context.Groups.AddRange(wolves, owls);
        context.GroupBonusPoints.AddRange(Points(owls, Av, 3), Points(owls, Rb, 4, DateTimeOffset.UtcNow));
        context.Assignments.Add(new Assignment { Code = "1", StandardPoints = 5, AwesomePoints = 10 });
        context.Submissions.Add(new Submission { GroupId = wolves.Id, AssignmentCode = "1", Status = SubmissionStatus.Approved });
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        var owlStats = stats.GroupStatistics.Single(g => g.GroupId == owls.Id);
        owlStats.Score.Should().Be(7);
        owlStats.BonusPoints.Should().Equal(new Dictionary<string, int> { [Av] = 3, [Rb] = 4 });
        owlStats.Rank.Should().Be(1, "7 bonus points beat 5 assignment points");
        stats.GroupStatistics.Single(g => g.GroupId == wolves.Id).Rank.Should().Be(2);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldPutEachBonusOnTheChartWithItsCategory()
    {
        await using var context = NewContext();
        var changed = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2" };
        context.Groups.Add(owls);
        context.GroupBonusPoints.Add(Points(owls, Rb, 4, changed));
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        // No Avonturiers points: no event for them
        stats.ScoreEvents.Should().ContainSingle()
            .Which.Should().Be(new ScoreEvent(owls.Id, "", changed, 4, "Reisbureau (pubquiz)"));
        stats.ScoreEvents.Sum(e => e.Points).Should().Be(stats.GroupStatistics.Single().Score, "the chart total matches the scoreboard");
    }

    [Fact]
    public async Task SetBonusPointsAsync_ShouldOnlyMoveTheTimeOfACategoryThatChanged()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 18, 0, 0, TimeSpan.Zero));
        var repository = new GroupRepository(context, time);

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 0, 10) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(2));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Rb, 0, 6) });

        var adventurers = (await StoredAsync(context, group, Av))!;
        adventurers.Points.Should().Be(10);
        adventurers.UpdatedAt.Should().Be(first, "Avonturiers did not change the second time");
        var travel = (await StoredAsync(context, group, Rb))!;
        travel.Points.Should().Be(6);
        travel.UpdatedAt.Should().Be(time.GetUtcNow());
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldPutEveryChangeOnTheChartAtItsOwnTime()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero));
        var repository = new GroupRepository(context, time);

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 0, 10) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(2));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 10, 25) });
        var second = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 25, 0) });
        var third = time.GetUtcNow();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(
            new ScoreEvent(group.Id, "", first, 10, "Avonturiers"),
            new ScoreEvent(group.Id, "", second, 15, "Avonturiers"),
            new ScoreEvent(group.Id, "", third, -25, "Avonturiers"));
        stats.ScoreEvents.Sum(e => e.Points).Should().Be(stats.GroupStatistics.Single().Score, "the chart total matches the scoreboard");
    }

    [Fact]
    public async Task SetBonusPointsAsync_PointsFromBeforeTheHistory_ShouldKeepTheirOwnTime()
    {
        await using var context = NewContext();
        var entered = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        // Entered before changes were kept (moved from the old Groups columns): a value without any history rows
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        context.GroupBonusPoints.Add(Points(group, Av, 10, entered));
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 16, 0, 0, TimeSpan.Zero));

        await new GroupRepository(context, time).SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 10, 25) });
        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(
            new ScoreEvent(group.Id, "", entered, 10, "Avonturiers"),
            new ScoreEvent(group.Id, "", time.GetUtcNow(), 15, "Avonturiers"));
        stats.ScoreEvents.Sum(e => e.Points).Should().Be(25);
    }

    [Fact]
    public async Task GetStatisticsAsync_TypoFixedQuickly_ShouldBeOneStepWithoutASpike()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero));
        var repository = new GroupRepository(context, time);

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 0, 100) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 100, 10) });

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(new ScoreEvent(group.Id, "", first, 10, "Avonturiers"));
        (await context.BonusPointsEntries.CountAsync()).Should().Be(2, "the history keeps both changes; only the chart merges them");
    }

    [Fact]
    public async Task GetStatisticsAsync_EnteredForTheWrongGroupAndUndone_ShouldLeaveNoStep()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero));
        var repository = new GroupRepository(context, time);

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Rb, 0, 8) });
        time.Advance(SubmissionRepository.BonusMergeWindow - TimeSpan.FromSeconds(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Rb, 8, 0) });

        (await new SubmissionRepository(context).GetStatisticsAsync()).ScoreEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatisticsAsync_ChangesFurtherApart_ShouldStaySeparateSteps()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero));
        var repository = new GroupRepository(context, time);

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Rb, 0, 5) });
        time.Advance(SubmissionRepository.BonusMergeWindow);
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Rb, 5, 12) });

        (await new SubmissionRepository(context).GetStatisticsAsync()).ScoreEvents.Select(e => e.Points).Should().Equal(5, 7);
    }

    [Fact]
    public async Task GetStatisticsAsync_HistoryAddsUpToMoreThanTheScore_ShouldFollowTheScoreWithoutDippingBelowZero()
    {
        await using var context = NewContext();
        var start = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        context.GroupBonusPoints.Add(Points(group, Av, 4, start.AddHours(1)));
        context.BonusPointsEntries.AddRange(
            new BonusPointsEntry { GroupId = group.Id, CategoryKey = Av, Delta = 10, Total = 10, ChangedAt = start },
            new BonusPointsEntry { GroupId = group.Id, CategoryKey = Av, Delta = -4, Total = 6, ChangedAt = start.AddHours(1) });
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Select(e => e.Points).Should().Equal(10, -6);
        stats.ScoreEvents.Sum(e => e.Points).Should().Be(stats.GroupStatistics.Single().Score);
    }

    [Fact]
    public async Task SetBonusPointsAsync_TwoAdminsAtOnce_ShouldSaveOneAndKeepOneHistoryRow()
    {
        using var temp = new TempDirectory();
        var options = new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseSqlite($"Data Source={Path.Combine(temp.Path, "bonus.db")}").Options;
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        await using (var db = new ScoutingDbContext(options))
        {
            await db.Database.EnsureCreatedAsync();
            db.Groups.Add(group);
            await db.SaveChangesAsync();
        }

        // Both tabs loaded 0 and save at the same moment, each with their own scope as the panel does
        async Task<bool> Save(int points)
        {
            await using var db = new ScoutingDbContext(options);
            try
            {
                await new GroupRepository(db).SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, Av, 0, points) });
                return true;
            }
            catch (BonusPointsConflictException)
            {
                return false;
            }
        }

        var results = await Task.WhenAll(Save(5), Save(7));

        results.Should().ContainSingle(r => r, "the second save sees the first one's value and refuses");
        await using (var db = new ScoutingDbContext(options))
        {
            var saved = (await db.GroupBonusPoints.SingleAsync()).Points;
            var entry = await db.BonusPointsEntries.SingleAsync();
            entry.Total.Should().Be(saved);
            entry.Delta.Should().Be(saved);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(BonusPointsChange.MaxPoints + 1)]
    public async Task SetBonusPointsAsync_OutOfRange_ShouldSaveNothing(int points)
    {
        await using var context = NewContext();
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2" };
        context.Groups.AddRange(wolves, owls);
        await context.SaveChangesAsync();

        var act = () => new GroupRepository(context).SetBonusPointsAsync(new[]
        {
            new BonusPointsChange(wolves.Id, Av, 0, 5),
            new BonusPointsChange(owls.Id, Av, 0, points)
        });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await context.GroupBonusPoints.CountAsync()).Should().Be(0, "a save is all or nothing");
    }

    [Fact]
    public async Task SetBonusPointsAsync_ChangedByAnotherAdmin_ShouldSaveNothing()
    {
        await using var context = NewContext();
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2" };
        context.Groups.AddRange(wolves, owls);
        context.GroupBonusPoints.Add(Points(owls, Rb, 8));
        await context.SaveChangesAsync();

        // This tab loaded the Bosuilen with 0 Reisbureau points; another admin entered 8 since
        var act = () => new GroupRepository(context).SetBonusPointsAsync(new[]
        {
            new BonusPointsChange(wolves.Id, Av, 0, 5),
            new BonusPointsChange(owls.Id, Rb, 0, 3)
        });

        (await act.Should().ThrowAsync<BonusPointsConflictException>()).Which.GroupName.Should().Be("De Bosuilen");
        (await StoredAsync(context, wolves, Av)).Should().BeNull();
        (await StoredAsync(context, owls, Rb))!.Points.Should().Be(8, "the other admin's entry is not undone");
    }

    [Fact]
    public async Task DeleteAllAsync_AsInARebuild_ShouldKeepBonusPoints()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        context.GroupBonusPoints.Add(Points(group, Av, 8));
        context.BonusPointsEntries.Add(new BonusPointsEntry { GroupId = group.Id, CategoryKey = Av, Delta = 8, Total = 8 });
        context.Submissions.Add(new Submission { GroupId = group.Id, AssignmentCode = "1", Status = SubmissionStatus.Approved });
        await context.SaveChangesAsync();

        await new SubmissionRepository(context).DeleteAllAsync();

        (await context.Submissions.CountAsync()).Should().Be(0);
        (await StoredAsync(context, group, Av))!.Points.Should().Be(8);
        (await context.BonusPointsEntries.CountAsync()).Should().Be(1, "the history is not from Slack, so a rebuild keeps it");
    }

    [Fact]
    public async Task GetStatisticsAsync_CategoryAddedInTheApp_ShouldCountWithItsName()
    {
        using var temp = new TempDirectory();
        var runtime = Runtime(temp);
        runtime.AddBonusCategory("fotowedstrijd", "Fotowedstrijd", "Fw").Should().BeNull();
        await using var context = NewContext();
        var at = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        context.GroupBonusPoints.AddRange(Points(group, Av, 2), Points(group, "fotowedstrijd", 5, at));
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context, runtime).GetStatisticsAsync();

        var wolves = stats.GroupStatistics.Single();
        wolves.Score.Should().Be(7);
        wolves.BonusPoints.Keys.Should().Equal(Av, Rb, "fotowedstrijd");
        stats.ScoreEvents.Should().Contain(new ScoreEvent(group.Id, "", at, 5, "Fotowedstrijd"));
    }

    [Fact]
    public async Task GetStatisticsAsync_CategoryRemoved_ShouldNotCountButKeepThePointsForWhenItIsBack()
    {
        using var temp = new TempDirectory();
        var runtime = Runtime(temp);
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        context.Groups.Add(group);
        context.GroupBonusPoints.AddRange(Points(group, Av, 3), Points(group, Rb, 4, DateTimeOffset.UtcNow));
        await context.SaveChangesAsync();
        var repository = new SubmissionRepository(context, runtime);

        runtime.RemoveBonusCategory(Rb).Should().BeTrue();
        var withoutTravel = await repository.GetStatisticsAsync();

        withoutTravel.GroupStatistics.Single().Score.Should().Be(3);
        withoutTravel.GroupStatistics.Single().BonusPoints.Keys.Should().Equal(Av);
        withoutTravel.ScoreEvents.Should().NotContain(e => e.Label == "Reisbureau (pubquiz)");
        (await StoredAsync(context, group, Rb))!.Points.Should().Be(4, "removing a category hides its points but keeps them");

        runtime.AddBonusCategory(Rb, "Pubquiz").Should().BeNull();
        var back = await repository.GetStatisticsAsync();

        back.GroupStatistics.Single().Score.Should().Be(7);
        back.ScoreEvents.Should().Contain(e => e.Label == "Pubquiz" && e.Points == 4);
    }

    private static RuntimeSettingsService Runtime(TempDirectory temp) =>
        new(Options.Create(new ScoutingSettings { RuntimeSettingsFile = Path.Combine(temp.Path, "runtime-settings.json") }),
            NullLogger<RuntimeSettingsService>.Instance);
}
