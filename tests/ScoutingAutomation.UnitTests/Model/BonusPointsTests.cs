using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using Xunit;

namespace ScoutingAutomation.UnitTests.Model;

/// <summary>Avonturiers and Reisbureau points, entered by hand per group.</summary>
public class BonusPointsTests
{
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static ScoutingDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ScoutingDbContext>().UseInMemoryDatabase($"Bonus_{Guid.NewGuid():N}").Options);

    [Fact]
    public async Task GetStatisticsAsync_ShouldAddBonusToScoreAndRank()
    {
        await using var context = NewContext();
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2", AdventurersPoints = 3, TravelAgencyPoints = 4, TravelAgencyPointsUpdatedAt = DateTimeOffset.UtcNow };
        context.Groups.AddRange(wolves, owls);
        context.Assignments.Add(new Assignment { Number = 1, StandardPoints = 5, AwesomePoints = 10 });
        context.Submissions.Add(new Submission { GroupId = wolves.Id, AssignmentNumber = 1, Status = SubmissionStatus.Approved });
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        var owlStats = stats.GroupStatistics.Single(g => g.GroupId == owls.Id);
        owlStats.Score.Should().Be(7);
        owlStats.AdventurersPoints.Should().Be(3);
        owlStats.TravelAgencyPoints.Should().Be(4);
        owlStats.Rank.Should().Be(1, "7 bonus points beat 5 assignment points");
        stats.GroupStatistics.Single(g => g.GroupId == wolves.Id).Rank.Should().Be(2);
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldPutEachBonusOnTheChartWithItsCategory()
    {
        await using var context = NewContext();
        var changed = new DateTimeOffset(2026, 10, 3, 20, 0, 0, TimeSpan.Zero);
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2", TravelAgencyPoints = 4, TravelAgencyPointsUpdatedAt = changed };
        context.Groups.Add(owls);
        await context.SaveChangesAsync();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        // No Avonturiers points: no event for them
        stats.ScoreEvents.Should().ContainSingle()
            .Which.Should().Be(new Domain.Models.ScoreEvent(owls.Id, 0, changed, 4, "Reisbureau (pubquiz)"));
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

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, 10, 0) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(2));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 10, 0, 10, 6) });

        group.AdventurersPoints.Should().Be(10);
        group.AdventurersPointsUpdatedAt.Should().Be(first, "Avonturiers did not change the second time");
        group.TravelAgencyPoints.Should().Be(6);
        group.TravelAgencyPointsUpdatedAt.Should().Be(time.GetUtcNow());
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

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, 10, 0) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(2));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 10, 0, 25, 0) });
        var second = time.GetUtcNow();
        time.Advance(TimeSpan.FromHours(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 25, 0, 0, 0) });
        var third = time.GetUtcNow();

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(
            new ScoreEvent(group.Id, 0, first, 10, "Avonturiers"),
            new ScoreEvent(group.Id, 0, second, 15, "Avonturiers"),
            new ScoreEvent(group.Id, 0, third, -25, "Avonturiers"));
        stats.ScoreEvents.Sum(e => e.Points).Should().Be(stats.GroupStatistics.Single().Score, "the chart total matches the scoreboard");
    }

    [Fact]
    public async Task SetBonusPointsAsync_PointsFromBeforeTheHistory_ShouldKeepTheirOwnTime()
    {
        await using var context = NewContext();
        var entered = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        // Entered before changes were kept: a value without any history rows
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1", AdventurersPoints = 10, AdventurersPointsUpdatedAt = entered };
        context.Groups.Add(group);
        await context.SaveChangesAsync();
        var time = new ManualClock(new DateTimeOffset(2026, 10, 3, 16, 0, 0, TimeSpan.Zero));

        await new GroupRepository(context, time).SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 10, 0, 25, 0) });
        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(
            new ScoreEvent(group.Id, 0, entered, 10, "Avonturiers"),
            new ScoreEvent(group.Id, 0, time.GetUtcNow(), 15, "Avonturiers"));
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

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, 100, 0) });
        var first = time.GetUtcNow();
        time.Advance(TimeSpan.FromMinutes(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 100, 0, 10, 0) });

        var stats = await new SubmissionRepository(context).GetStatisticsAsync();

        stats.ScoreEvents.Should().Equal(new ScoreEvent(group.Id, 0, first, 10, "Avonturiers"));
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

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, 0, 8) });
        time.Advance(SubmissionRepository.BonusMergeWindow - TimeSpan.FromSeconds(1));
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 8, 0, 0) });

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

        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, 0, 5) });
        time.Advance(SubmissionRepository.BonusMergeWindow);
        await repository.SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 5, 0, 12) });

        (await new SubmissionRepository(context).GetStatisticsAsync()).ScoreEvents.Select(e => e.Points).Should().Equal(5, 7);
    }

    [Fact]
    public async Task GetStatisticsAsync_HistoryAddsUpToMoreThanTheScore_ShouldFollowTheScoreWithoutDippingBelowZero()
    {
        await using var context = NewContext();
        var start = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.Zero);
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1", AdventurersPoints = 4, AdventurersPointsUpdatedAt = start.AddHours(1) };
        context.Groups.Add(group);
        context.BonusPointsEntries.AddRange(
            new BonusPointsEntry { GroupId = group.Id, Category = BonusCategory.Adventurers, Delta = 10, Total = 10, ChangedAt = start },
            new BonusPointsEntry { GroupId = group.Id, Category = BonusCategory.Adventurers, Delta = -4, Total = 6, ChangedAt = start.AddHours(1) });
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
                await new GroupRepository(db).SetBonusPointsAsync(new[] { new BonusPointsChange(group.Id, 0, 0, points, 0) });
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
            var saved = (await db.Groups.SingleAsync()).AdventurersPoints;
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
            new BonusPointsChange(wolves.Id, 0, 0, 5, 5),
            new BonusPointsChange(owls.Id, 0, 0, points, 0)
        });

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
        wolves.AdventurersPoints.Should().Be(0, "a save is all or nothing");
    }

    [Fact]
    public async Task SetBonusPointsAsync_ChangedByAnotherAdmin_ShouldSaveNothing()
    {
        await using var context = NewContext();
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "C2", TravelAgencyPoints = 8 };
        context.Groups.AddRange(wolves, owls);
        await context.SaveChangesAsync();

        // This tab loaded the Bosuilen with 0 Reisbureau points; another admin entered 8 since
        var act = () => new GroupRepository(context).SetBonusPointsAsync(new[]
        {
            new BonusPointsChange(wolves.Id, 0, 0, 5, 0),
            new BonusPointsChange(owls.Id, 0, 0, 3, 0)
        });

        (await act.Should().ThrowAsync<BonusPointsConflictException>()).Which.GroupName.Should().Be("De Bosuilen");
        wolves.AdventurersPoints.Should().Be(0);
        owls.TravelAgencyPoints.Should().Be(8, "the other admin's entry is not undone");
    }

    [Fact]
    public async Task DeleteAllAsync_AsInARebuild_ShouldKeepBonusPoints()
    {
        await using var context = NewContext();
        var group = new Group { Name = "De Wolven", SlackChannelId = "C1", AdventurersPoints = 8 };
        context.Groups.Add(group);
        context.BonusPointsEntries.Add(new BonusPointsEntry { GroupId = group.Id, Category = BonusCategory.Adventurers, Delta = 8, Total = 8 });
        context.Submissions.Add(new Submission { GroupId = group.Id, AssignmentNumber = 1, Status = SubmissionStatus.Approved });
        await context.SaveChangesAsync();

        await new SubmissionRepository(context).DeleteAllAsync();

        (await context.Submissions.CountAsync()).Should().Be(0);
        (await context.Groups.SingleAsync()).AdventurersPoints.Should().Be(8);
        (await context.BonusPointsEntries.CountAsync()).Should().Be(1, "the history is not from Slack, so a rebuild keeps it");
    }
}
