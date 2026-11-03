using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

/// <summary>The unlock rule: 30 of 160 open at the start, the rest in proportion to the 100 locations.</summary>
public class AssignmentUnlocksTests
{
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly IReadOnlyList<string> Codes = Enumerable.Range(1, 160).Select(n => n.ToString()).ToList();
    private static readonly IReadOnlyCollection<int> Locations = Enumerable.Range(1, 100).ToHashSet();

    private static Submission CheckIn(int location, SubmissionStatus status = SubmissionStatus.Approved) =>
        new() { GroupId = Group, Kind = SubmissionKind.CheckIn, AssignmentCode = location.ToString(), Status = status };

    [Theory]
    [InlineData(0, 30)]
    [InlineData(1, 31)]
    [InlineData(2, 32)]
    [InlineData(3, 33)]
    [InlineData(4, 35)]
    [InlineData(10, 43)]
    [InlineData(50, 95)]
    [InlineData(99, 158)]
    [InlineData(100, 160)]
    public void OpenCount_ShouldFollowTheShareOfLocations(int counted, int expected)
    {
        AssignmentUnlocks.OpenCount(30, 160, counted, 100).Should().Be(expected);
    }

    [Fact]
    public void OpenCount_EachLocation_ShouldOpenOneOrTwo()
    {
        var steps = Enumerable.Range(1, 100)
            .Select(k => AssignmentUnlocks.OpenCount(30, 160, k, 100) - AssignmentUnlocks.OpenCount(30, 160, k - 1, 100))
            .ToList();

        steps.Should().OnlyContain(s => s == 1 || s == 2);
        steps.Sum().Should().Be(130);
    }

    [Theory]
    [InlineData(30, 160, 5, 0, 160)]
    [InlineData(200, 160, 0, 100, 160)]
    [InlineData(-5, 160, 0, 100, 0)]
    [InlineData(30, 0, 5, 100, 0)]
    [InlineData(30, 160, 500, 100, 160)]
    public void OpenCount_EdgeCases(int initial, int numbered, int counted, int locations, int expected)
    {
        AssignmentUnlocks.OpenCount(initial, numbered, counted, locations).Should().Be(expected);
    }

    [Fact]
    public void Compute_ShouldOnlyCountApprovedCheckInsAtOpenLocations()
    {
        var checkIns = new[]
        {
            CheckIn(3), CheckIn(3), // twice the same location counts once
            CheckIn(5, SubmissionStatus.Awesome),
            CheckIn(7, SubmissionStatus.Rejected),
            CheckIn(8, SubmissionStatus.Pending),
            CheckIn(12) // not open yet
        };

        var state = AssignmentUnlocks.Compute(Group, checkIns, openLocations: 10, Locations, Codes, initialOpen: 30);

        state.ApprovedLocations.Should().Equal("3", "5", "12");
        state.CountedLocations.Should().Equal("3", "5");
        state.OpenCount.Should().Be(32);
        state.Total.Should().Be(160);
        state.IsOpen("32").Should().BeTrue();
        state.IsOpen("33").Should().BeFalse();
        state.IsLocationOpen(10).Should().BeTrue();
        state.IsLocationOpen(11).Should().BeFalse();
        state.NextLocationOpens.Should().Be(1, "the third location takes the count from 32 to 33");
    }

    [Fact]
    public void Compute_LettersAreAlwaysOpenAndNotCounted()
    {
        var state = AssignmentUnlocks.Compute(Group, Array.Empty<Submission>(), 10, Locations, Codes.Append("A").Append("B"), 30);

        state.Total.Should().Be(160);
        state.IsOpen("A").Should().BeTrue();
        state.OpenCodes.Should().HaveCount(32).And.Contain(new[] { "1", "30", "A", "B" }).And.NotContain("31");
    }

    [Fact]
    public void Compute_CheckInAtADeletedLocation_ShouldNotCount()
    {
        var state = AssignmentUnlocks.Compute(Group, new[] { CheckIn(3) }, 10, new HashSet<int> { 1, 2 }, Codes, 30);

        state.CountedLocations.Should().BeEmpty();
    }

    [Fact]
    public void Compute_WithoutLocations_EverythingIsOpen()
    {
        var state = AssignmentUnlocks.Compute(Group, Array.Empty<Submission>(), 10, Array.Empty<int>(), Codes, 30);

        state.OpenCount.Should().Be(160);
        state.IsOpen("160").Should().BeTrue();
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 10, 0, 0, TimeSpan.Zero);

    // Round 1 10:00-10:30, round 2 11:00-11:30
    private static readonly IReadOnlyList<ThemeRound> Rounds =
    [
        new(1, T0, T0.AddMinutes(30), ["1"], null),
        new(2, T0.AddMinutes(60), T0.AddMinutes(90), ["2"], null)
    ];

    [Theory]
    [InlineData(-10, 30)] // before the first round: the end of round 1
    [InlineData(0, 30)]
    [InlineData(29, 30)]
    [InlineData(30, 90)] // the end is not part of the round
    [InlineData(45, 90)] // between rounds: the end of round 2
    [InlineData(89, 90)]
    [InlineData(95, 95)] // after the last round: at once
    public void ReleaseAt_ShouldBeTheFirstRoundEndAfterThePost(int postedMinute, int expectedMinute)
    {
        AssignmentUnlocks.ReleaseAt(T0.AddMinutes(postedMinute), Rounds).Should().Be(T0.AddMinutes(expectedMinute));
    }

    [Fact]
    public void ReleaseAt_WithoutRounds_ShouldBeAtOnce()
    {
        AssignmentUnlocks.ReleaseAt(T0, Array.Empty<ThemeRound>()).Should().Be(T0);
    }

    [Fact]
    public void Compute_CheckInDuringARound_ShouldOnlyCountFromTheEndOfThatRound()
    {
        var checkIns = new[] { CheckInAt(3, 10), CheckInAt(4, 40) };

        var during = AssignmentUnlocks.Compute(Group, checkIns, 10, Locations, Codes, 30, Rounds, T0.AddMinutes(20));
        during.ApprovedLocations.Should().Equal("3", "4");
        during.CountedLocations.Should().BeEmpty();
        during.WaitingLocations.Should().Equal("3", "4");
        during.NextReleaseAt.Should().Be(T0.AddMinutes(30));
        during.OpenCount.Should().Be(30);

        var after = AssignmentUnlocks.Compute(Group, checkIns, 10, Locations, Codes, 30, Rounds, T0.AddMinutes(40));
        after.CountedLocations.Should().Equal("3");
        after.WaitingLocations.Should().Equal("4");
        after.NextReleaseAt.Should().Be(T0.AddMinutes(90), "location 4 was posted between the rounds");
        after.OpenCount.Should().Be(31);

        var last = AssignmentUnlocks.Compute(Group, checkIns, 10, Locations, Codes, 30, Rounds, T0.AddMinutes(90));
        last.CountedLocations.Should().Equal("3", "4");
        last.WaitingLocations.Should().BeEmpty();
        last.NextReleaseAt.Should().BeNull();
    }

    [Fact]
    public void Compute_LocationWithAnEarlierRejectedCheckIn_ShouldWaitForTheApprovedOne()
    {
        var checkIns = new[] { CheckInAt(3, 10, SubmissionStatus.Rejected), CheckInAt(3, 40) };

        var state = AssignmentUnlocks.Compute(Group, checkIns, 10, Locations, Codes, 30, Rounds, T0.AddMinutes(40));

        state.CountedLocations.Should().BeEmpty();
        state.NextReleaseAt.Should().Be(T0.AddMinutes(90));
    }

    private static Submission CheckInAt(int location, int minute, SubmissionStatus status = SubmissionStatus.Approved)
    {
        var checkIn = CheckIn(location, status);
        checkIn.CreatedAt = T0.AddMinutes(minute);
        return checkIn;
    }

    [Fact]
    public void Compute_SuperLocation_ShouldCountTwice()
    {
        var super = new HashSet<int> { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };

        var normal = AssignmentUnlocks.Compute(Group, new[] { CheckIn(3) }, 100, Locations, Codes, 30, superLocations: super);
        var doubled = AssignmentUnlocks.Compute(Group, new[] { CheckIn(10) }, 100, Locations, Codes, 30, superLocations: super);

        // 130 to open over 110 (90 normal + 10 super counted twice)
        normal.OpenCount.Should().Be(30 + 130 * 1 / 110);
        doubled.OpenCount.Should().Be(30 + 130 * 2 / 110);
        doubled.CountedLocations.Should().Equal("10");
        normal.NextSuperLocationOpens.Should().Be(AssignmentUnlocks.OpenCount(30, 160, 3, 110) - normal.OpenCount);
    }

    [Fact]
    public void Compute_AllLocationsWithSuperLocations_ShouldOpenEverything()
    {
        var super = new HashSet<int> { 10, 20, 999 };
        var all = Locations.Select(n => CheckIn(n)).ToList();

        var state = AssignmentUnlocks.Compute(Group, all, 100, Locations, Codes, 30, superLocations: super);

        state.OpenCount.Should().Be(160);
        state.NextLocationOpens.Should().Be(0);
        state.NextSuperLocationOpens.Should().Be(0);
    }

    [Fact]
    public void Compute_WithoutSuperLocationsLeft_ShouldNotAnnounceOne()
    {
        var state = AssignmentUnlocks.Compute(Group, new[] { CheckIn(10) }, 100, Locations, Codes, 30, superLocations: new HashSet<int> { 10 });

        state.NextSuperLocationOpens.Should().Be(0);
        state.NextLocationOpens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Compute_SuperLocationNotOpenForTheGroup_ShouldNotCountUntilOpened()
    {
        var super = new HashSet<int> { 20 };

        var closed = AssignmentUnlocks.Compute(Group, new[] { CheckIn(20) }, 10, Locations, Codes, 30, superLocations: super);
        var opened = AssignmentUnlocks.Compute(Group, new[] { CheckIn(20) }, 20, Locations, Codes, 30, superLocations: super);

        closed.OpenCount.Should().Be(30);
        closed.NextSuperLocationOpens.Should().Be(0, "no super location is open for the group");
        opened.OpenCount.Should().Be(AssignmentUnlocks.OpenCount(30, 160, 2, 101));
    }

    [Fact]
    public void Compute_SuperLocationWaitingForItsRound_ShouldNotBeLeftToVisit()
    {
        var checkIns = new[] { CheckInAt(10, 10) };

        var state = AssignmentUnlocks.Compute(Group, checkIns, 10, Locations, Codes, 30, Rounds, T0.AddMinutes(20), new HashSet<int> { 10 });

        state.WaitingLocations.Should().Equal("10");
        state.NextSuperLocationOpens.Should().Be(0, "the only super location is visited, it waits for the end of the round");
        state.NextLocationOpens.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Compute_SuperNumberThatIsNoLocation_ShouldChangeNothing()
    {
        var with = AssignmentUnlocks.Compute(Group, new[] { CheckIn(3) }, 10, Locations, Codes, 30, superLocations: new HashSet<int> { 500 });
        var without = AssignmentUnlocks.Compute(Group, new[] { CheckIn(3) }, 10, Locations, Codes, 30);

        with.Should().BeEquivalentTo(without, o => o.Excluding(s => s.GroupId));
        with.NextSuperLocationOpens.Should().Be(0);
    }

    [Fact]
    public void Compute_CheckInWithoutANumber_ShouldNotThrow()
    {
        var odd = new Submission { GroupId = Group, Kind = SubmissionKind.CheckIn, AssignmentCode = "X", Status = SubmissionStatus.Approved };

        var state = AssignmentUnlocks.Compute(Group, new[] { odd, CheckIn(10) }, 10, Locations, Codes, 30, superLocations: new HashSet<int> { 10 });

        state.CountedLocations.Should().Equal("10");
    }

    [Fact]
    public void NewlyOpened_ShouldListTheCodesInNumberOrder()
    {
        var shuffled = Codes.Reverse().Append("A");

        AssignmentUnlocks.NewlyOpened(30, 32, shuffled).Should().Equal("31", "32");
        AssignmentUnlocks.NewlyOpened(32, 31, shuffled).Should().BeEmpty();
    }

    [Theory]
    [InlineData(new string[0], "")]
    [InlineData(new[] { "31" }, "Jullie kunnen nu opdracht 31 doen.")]
    [InlineData(new[] { "31", "32", "33" }, "Jullie kunnen nu opdracht 31, 32 en 33 doen.")]
    public void Notice_ShouldNameTheNewAssignments(string[] opened, string sentence)
    {
        UnlockNotifier.Render("{assignment}|{count}/{total}", 33, 160, opened).Should().Be($"{sentence}|33/160".Trim());
    }
}
