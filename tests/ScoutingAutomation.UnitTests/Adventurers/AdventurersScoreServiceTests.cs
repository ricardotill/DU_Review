using FluentAssertions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

/// <summary>The Avonturiers rules: unlocked assignments, round ranking, caps and the score service.</summary>
public class AdventurersScoreServiceTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();
    private static readonly DateTimeOffset Noon = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private static ThemeRound Round(int number, int startMinutes, params string[] themes) =>
        new(number, Noon.AddMinutes(startMinutes), Noon.AddMinutes(startMinutes + 30), themes, null);

    private static Submission Theme(Guid group, string theme, int minute, SubmissionStatus status = SubmissionStatus.Approved) =>
        new() { GroupId = group, Kind = SubmissionKind.Theme, AssignmentCode = theme, CreatedAt = Noon.AddMinutes(minute), Status = status };

    private static Submission CheckIn(Guid group, string location, SubmissionStatus status = SubmissionStatus.Approved) =>
        new() { GroupId = group, Kind = SubmissionKind.CheckIn, AssignmentCode = location, CreatedAt = Noon, Status = status };

    [Fact]
    public void Rank_ShouldGiveTwoAndOne()
    {
        AdventurersScoring.Rank(new[] { (A, 3), (B, 1), (C, 2) })
            .Select(t => (t.GroupId, t.Place, t.Points))
            .Should().Equal((A, 1, 2), (C, 2, 1), (B, 3, 0));
    }

    [Fact]
    public void Rank_Tie_ShouldSharePlaceAndSkipTheNext()
    {
        AdventurersScoring.Rank(new[] { (A, 3), (B, 3), (C, 2) })
            .Select(t => (t.GroupId, t.Place, t.Points))
            .Should().BeEquivalentTo(new[] { (A, 1, 2), (B, 1, 2), (C, 3, 0) });
    }

    [Fact]
    public void Rank_TieForSecond_ShouldGiveBothOnePoint()
    {
        AdventurersScoring.Rank(new[] { (A, 3), (B, 2), (C, 2) })
            .Select(t => t.Points).Should().Equal(2, 1, 1);
    }

    [Fact]
    public void Rank_ZeroApproved_ShouldGetNothing()
    {
        AdventurersScoring.Rank(new[] { (A, 0), (B, 0) }).Should().BeEmpty();
    }

    [Fact]
    public void ApprovedLocations_BestStatusWins_AndEachCountsOnce()
    {
        var submissions = new[]
        {
            CheckIn(A, "7", SubmissionStatus.Rejected),
            CheckIn(A, "7", SubmissionStatus.Approved),
            CheckIn(A, "7", SubmissionStatus.Awesome),
            CheckIn(A, "12", SubmissionStatus.Pending),
            CheckIn(A, "3", SubmissionStatus.Approved)
        };

        AdventurersScoring.ApprovedLocations(submissions).Should().Equal("3", "7");
    }

    [Fact]
    public void Compute_ShouldScoreOnlyThemesPostedInTheirRound()
    {
        var rounds = new[] { Round(1, 0, "1"), Round(2, 30, "2") };
        var submissions = new[]
        {
            Theme(A, "1", 5),
            Theme(B, "1", 10),
            Theme(B, "2", 12),   // theme of round 2, posted in round 1: stored, not scored
            Theme(B, "1", 40),   // theme of round 1, posted in round 2: too late
            Theme(C, "2", 35, SubmissionStatus.Rejected)
        };

        var standing = AdventurersScoring.Compute(new[] { A, B, C }, submissions, rounds, new Dictionary<Guid, int>(), 14, 14, Noon.AddHours(2));

        standing.Rounds[0].Teams.Select(t => (t.GroupId, t.Points)).Should().BeEquivalentTo(new[] { (A, 2), (B, 2) });
        standing.Rounds[1].Teams.Should().BeEmpty();
        standing.For(A)!.ThemePoints.Should().Be(2);
        standing.For(B)!.ThemePoints.Should().Be(2);
        standing.For(C)!.ThemePoints.Should().Be(0);
    }

    [Fact]
    public void Compute_RunningRound_ShouldNotCountYet()
    {
        var rounds = new[] { Round(1, 0, "1") };
        var standing = AdventurersScoring.Compute(new[] { A }, new[] { Theme(A, "1", 5) }, rounds, new Dictionary<Guid, int>(), 14, 14, Noon.AddMinutes(10));

        standing.For(A)!.ThemePoints.Should().Be(0);
        standing.For(A)!.RunningRoundPoints.Should().Be(2);
        standing.Rounds[0].IsRunning.Should().BeTrue();
    }

    [Fact]
    public void Compute_OpenSubmission_ShouldMarkTheRoundProvisional()
    {
        var rounds = new[] { Round(1, 0, "1") };
        var standing = AdventurersScoring.Compute(new[] { A, B }, new[] { Theme(A, "1", 5), Theme(B, "1", 6, SubmissionStatus.Pending) },
            rounds, new Dictionary<Guid, int>(), 14, 14, Noon.AddHours(1));

        standing.Rounds[0].IsProvisional.Should().BeTrue();
        standing.Rounds[0].OpenSubmissions.Should().Be(1);
    }

    [Fact]
    public void Compute_ShouldCapThemeAndFinalPoints()
    {
        // Eight closed rounds won by A: 16 points, capped at 14
        var rounds = Enumerable.Range(1, 8).Select(n => Round(n, (n - 1) * 30, n.ToString())).ToArray();
        var submissions = rounds.Select(r => Theme(A, r.Themes[0], (r.Number - 1) * 30 + 1)).ToArray();
        var final = new Dictionary<Guid, int> { [A] = 20 };

        var standing = AdventurersScoring.Compute(new[] { A }, submissions, rounds, final, 14, 14, Noon.AddHours(10));

        standing.For(A)!.ThemePoints.Should().Be(14);
        standing.For(A)!.FinalPoints.Should().Be(14);
        standing.For(A)!.Total.Should().Be(28);
    }

    [Fact]
    public void RoundEvents_ShouldStepAtEachRoundEndAndStopAtTheCap()
    {
        var rounds = Enumerable.Range(1, 3).Select(n => Round(n, (n - 1) * 30, n.ToString())).ToArray();
        var submissions = rounds.Select(r => Theme(A, r.Themes[0], (r.Number - 1) * 30 + 1)).ToArray();
        var standing = AdventurersScoring.Compute(new[] { A }, submissions, rounds, new Dictionary<Guid, int>(), 5, 14, Noon.AddHours(3));

        AdventurersScoring.RoundEvents(standing, 5, "Avonturiers")
            .Select(e => (e.At, e.Points))
            .Should().Equal((Noon.AddMinutes(30), 2), (Noon.AddMinutes(60), 2), (Noon.AddMinutes(90), 1));
    }

    [Fact]
    public void Ranking_ShouldReadLikeTheResultMessage()
    {
        var result = new RoundResult(Round(4, 0, "4"), AdventurersScoring.Rank(new[] { (A, 3), (B, 2) }), true, false, false, 0);
        var names = new Dictionary<Guid, string> { [A] = "Team A", [B] = "Team B" };

        result.Ranking(id => names[id]).Should().Be("1. Team A (3), 2. Team B (2)");
    }

    [Fact]
    public async Task ScoreService_ShouldCombineRoundsUnlocksAndFinalPoints()
    {
        await using var db = TestDb.Create();
        // Locations 1 through 7 are open: the check-in at 8 is approved but does not count yet
        var groupA = new Group { Id = A, Name = "A", SlackChannelId = "CA", OpenLocations = 7 };
        db.Groups.Add(groupA);
        db.Submissions.AddRange(CheckIn(A, "7"), CheckIn(A, "8"), Theme(A, "1", 5),
            new Submission { GroupId = A, Kind = SubmissionKind.Assignment, AssignmentCode = "7", Status = SubmissionStatus.Approved, CreatedAt = Noon });
        db.GroupBonusPoints.Add(new GroupBonusPoints { GroupId = A, CategoryKey = "avonturiers", Points = 5 });
        db.Locations.AddRange(Enumerable.Range(1, 10).Select(n => new Location { Number = n, Name = $"L{n}", Latitude = 51, Longitude = 5 }));
        db.Assignments.AddRange(Enumerable.Range(1, 20).Select(n => new Assignment { Code = n.ToString(), StandardPoints = 1, AwesomePoints = 2 }));
        await db.SaveChangesAsync();

        var settings = new ScoutingSettings { Adventurers = new AdventurersSettings { Enabled = true, InitialOpenAssignments = 10 } };
        settings.Adventurers.Rounds.Add(new ThemeRoundConfig { Number = 1, Start = new DateTime(2026, 10, 3, 12, 0, 0), Themes = { "1" } });
        var runtime = TestRuntime.For(Options.Create(settings));
        var time = new FixedTimeProvider(Noon.AddHours(2));
        var unlocks = new AssignmentUnlockService(new GroupRepository(db), new LocationRepository(db), new AssignmentRepository(db),
            new SubmissionRepository(db), runtime);

        var overview = await new AdventurersScoreService(db, runtime, time, unlocks).GetOverviewAsync();

        var score = overview.Standing.For(A)!;
        score.ApprovedLocations.Should().Equal("7", "8");
        score.ThemePoints.Should().Be(2);
        score.FinalPoints.Should().Be(5);
        score.Total.Should().Be(7);
        var unlock = overview.UnlocksOf(A)!;
        unlock.CountedLocations.Should().Equal("7");
        unlock.OpenCount.Should().Be(11, "10 open at the start, plus 10 more spread over 10 locations");
        overview.IsLocationOpen(A, 7).Should().BeTrue();
        overview.IsLocationOpen(A, 8).Should().BeFalse();
        overview.Submissions.Should().OnlyContain(s => s.Kind != SubmissionKind.Assignment);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
