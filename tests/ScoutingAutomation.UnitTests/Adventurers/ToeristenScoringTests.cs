using FluentAssertions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Web.Standings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

/// <summary>Check-ins and themes share the Submissions table, but must never count as Toeristen assignments.</summary>
public class ToeristenScoringTests : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _temp = new();
    private readonly Group _group = new() { Name = "De Wolven", SlackChannelId = "C1", AdventurersSlackChannelId = "CAV1" };

    public void Dispose() => _temp.Dispose();

    private ScoutingSettings Settings(bool adventurers) => new()
    {
        RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
        Adventurers = new AdventurersSettings
        {
            Enabled = adventurers,
            ThemeMaxPoints = 3,
            FinalMaxPoints = 4,
            Rounds = { new ThemeRoundConfig { Number = 1, Start = new DateTime(2026, 10, 3, 12, 0, 0), Themes = { "1" } } }
        }
    };

    private Submission Make(SubmissionKind kind, string code, SubmissionStatus status, int minute = 5) => new()
    {
        GroupId = _group.Id, Kind = kind, AssignmentCode = code, Status = status, CreatedAt = Noon.AddMinutes(minute),
        SlackChannelId = kind == SubmissionKind.Assignment ? "C1" : "CAV1", SlackMessageId = Guid.NewGuid().ToString()
    };

    private async Task<Infrastructure.Data.ScoutingDbContext> SeedAsync()
    {
        var db = TestDb.Create();
        db.Groups.Add(_group);
        db.Assignments.Add(new Assignment { Code = "1", StandardPoints = 10, AwesomePoints = 20 });
        db.Submissions.AddRange(
            Make(SubmissionKind.Assignment, "1", SubmissionStatus.Approved),
            Make(SubmissionKind.CheckIn, "1", SubmissionStatus.Awesome),
            Make(SubmissionKind.Theme, "1", SubmissionStatus.Awesome),
            Make(SubmissionKind.CheckIn, "2", SubmissionStatus.Pending));
        db.GroupBonusPoints.Add(new GroupBonusPoints { GroupId = _group.Id, CategoryKey = "avonturiers", Points = 9 });
        await db.SaveChangesAsync();
        return db;
    }

    [Fact]
    public async Task Statistics_ShouldCountOnlyToeristenSubmissions()
    {
        await using var db = await SeedAsync();
        var runtime = TestRuntime.For(Options.Create(Settings(adventurers: false)));

        var stats = await new SubmissionRepository(db, runtime, new FixedTimeProvider(Noon.AddHours(2))).GetStatisticsAsync();

        var wolves = stats.GroupStatistics.Single();
        wolves.TotalSubmissions.Should().Be(1);
        wolves.ApprovedCount.Should().Be(1);
        wolves.AwesomeCount.Should().Be(0, "an awesome check-in is no awesome assignment");
        wolves.PendingCount.Should().Be(0);
        wolves.AdventurersPoints.Should().BeNull("the Avonturiers part is off");
        // Off: the hand-entered value counts as entered
        wolves.Score.Should().Be(10 + 9);
    }

    [Fact]
    public async Task Statistics_WithAdventurers_ShouldReplaceTheFinalCategoryByTheCappedTotal()
    {
        await using var db = await SeedAsync();
        var runtime = TestRuntime.For(Options.Create(Settings(adventurers: true)));

        var stats = await new SubmissionRepository(db, runtime, new FixedTimeProvider(Noon.AddHours(2))).GetStatisticsAsync();

        var wolves = stats.GroupStatistics.Single();
        // Round 1 won (2 points, cap 3) + final game 9 capped at 4
        wolves.AdventurersPoints.Should().Be(6);
        wolves.BonusPoints["avonturiers"].Should().Be(6);
        wolves.Score.Should().Be(10 + 6);
        stats.ScoreEvents.Where(e => e.GroupId == _group.Id).Sum(e => e.Points).Should().Be(16, "the chart ends at the score");
    }

    [Fact]
    public void AssignmentResult_ShouldIgnoreCheckInsAndThemes()
    {
        var submissions = new[]
        {
            Make(SubmissionKind.CheckIn, "1", SubmissionStatus.Approved),
            Make(SubmissionKind.Theme, "2", SubmissionStatus.Approved),
            Make(SubmissionKind.Assignment, "1", SubmissionStatus.Rejected)
        };
        var assignments = new Dictionary<string, Assignment> { ["1"] = new() { Code = "1", StandardPoints = 10, AwesomePoints = 20 } };

        var results = AssignmentResult.For(submissions, assignments);

        results.Should().ContainSingle();
        results[0].BestStatus.Should().Be(SubmissionStatus.Rejected);
        results[0].Points.Should().Be(0);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
