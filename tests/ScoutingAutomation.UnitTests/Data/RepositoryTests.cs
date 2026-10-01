using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Repositories;
using Xunit;

namespace ScoutingAutomation.UnitTests.Data;

public class GroupRepositoryTests
{
    [Fact]
    public async Task Lookups_ShouldFindGroupByIdChannelAndNameIgnoringCase()
    {
        await using var db = TestDb.Create();
        var repo = new GroupRepository(db);
        var group = new Group { Name = "De Wolven", SlackChannelId = "C03WOLVEN" };
        await repo.UpsertAsync(group);

        (await repo.GetByIdAsync(group.Id))!.Name.Should().Be("De Wolven");
        (await repo.GetBySlackChannelIdAsync("C03WOLVEN"))!.Id.Should().Be(group.Id);
        (await repo.GetByNameAsync("de wolven"))!.Id.Should().Be(group.Id);
        (await repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
        (await repo.GetAllAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task UpsertAsync_ExistingChannel_ShouldUpdateGroup()
    {
        await using var db = TestDb.Create();
        var repo = new GroupRepository(db);
        await repo.UpsertAsync(new Group { Name = "Oud", SlackChannelId = "C1" });

        await repo.UpsertAsync(new Group { Name = "Nieuw", Abbreviation = "NW", Description = "d", SlackChannelId = "C1" });

        var all = await repo.GetAllAsync();
        all.Should().ContainSingle();
        all[0].Name.Should().Be("Nieuw");
        all[0].Abbreviation.Should().Be("NW");
        all[0].Description.Should().Be("d");
    }
}

public class AssignmentRepositoryTests
{
    [Fact]
    public async Task UpsertAsync_ShouldInsertThenUpdatePoints()
    {
        await using var db = TestDb.Create();
        var repo = new AssignmentRepository(db);

        await repo.UpsertAsync(new Assignment { Number = 2, StandardPoints = 1, AwesomePoints = 2 });
        await repo.UpsertAsync(new Assignment { Number = 1, StandardPoints = 1, AwesomePoints = 2 });
        await repo.UpsertAsync(new Assignment { Number = 2, StandardPoints = 3, AwesomePoints = 6 });

        var all = await repo.GetAllAsync();
        all.Select(a => a.Number).Should().Equal(1, 2);
        var updated = await repo.GetByNumberAsync(2);
        updated!.StandardPoints.Should().Be(3);
        updated.AwesomePoints.Should().Be(6);
        (await repo.GetByNumberAsync(99)).Should().BeNull();
    }
}

public class SubmissionRepositoryTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 30, 10, 0, 0, TimeSpan.Zero);

    private static async Task<(SubmissionRepository Repo, Group Wolves, Group Owls, Submission[] Subs)> SeedAsync(ScoutingAutomation.Infrastructure.Data.ScoutingDbContext db)
    {
        var wolves = new Group { Name = "De Wolven", SlackChannelId = "CW" };
        var owls = new Group { Name = "De Bosuilen", SlackChannelId = "CB" };
        db.Groups.AddRange(wolves, owls);

        var approved = new Submission
        {
            GroupId = wolves.Id, AssignmentNumber = 1, Status = SubmissionStatus.Approved, SlackChannelId = "CW",
            SlackMessageId = "1.0", FileName = "kampvuur.jpg", Submitter = "Tim", MessageText = "Opdracht 1 vuur", CreatedAt = BaseTime
        };
        approved.Reviews.Add(new Review { Reviewer = "Sara_Jury", Status = SubmissionStatus.Approved, CreatedAt = BaseTime.AddMinutes(1) });
        approved.Reviews.Add(new Review { Reviewer = "Mark_Jury", Status = SubmissionStatus.Pending, CreatedAt = BaseTime.AddMinutes(5) });

        var pending = new Submission
        {
            GroupId = owls.Id, AssignmentNumber = 2, Status = SubmissionStatus.Pending, SlackChannelId = "CB",
            SlackMessageId = "2.0", FileName = "toren.png", Submitter = "Emma", CreatedAt = BaseTime.AddHours(1)
        };
        var older = new Submission
        {
            GroupId = owls.Id, AssignmentNumber = 1, Status = SubmissionStatus.Rejected, SlackChannelId = "CB",
            SlackMessageId = "3.0", FileName = "knopen.mp4", Submitter = "Daan", CreatedAt = BaseTime.AddDays(-1)
        };
        db.Submissions.AddRange(approved, pending, older);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        return (new SubmissionRepository(db), wolves, owls, new[] { approved, pending, older });
    }

    [Fact]
    public async Task GetByIdAsync_ShouldIncludeGroupAndOrderReviewsNewestFirst()
    {
        await using var db = TestDb.Create();
        var (repo, wolves, _, subs) = await SeedAsync(db);

        var result = await repo.GetByIdAsync(subs[0].Id);

        result!.Group!.Name.Should().Be(wolves.Name);
        result.Reviews.Select(r => r.Reviewer).Should().Equal("Mark_Jury", "Sara_Jury");
        (await repo.GetByIdAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task SlackLookups_ShouldFindByMessage()
    {
        await using var db = TestDb.Create();
        var (repo, _, _, subs) = await SeedAsync(db);

        var found = await repo.GetBySlackMessageIdAsync("CW", "1.0");
        found.Should().ContainSingle().Which.Id.Should().Be(subs[0].Id);
        found[0].Reviews.Select(r => r.Reviewer).Should().Equal("Mark_Jury", "Sara_Jury");
        (await repo.GetBySlackMessageIdAsync("CW", "9.9")).Should().BeEmpty();
        (await repo.ExistsAsync("CB", "2.0", null, "toren.png")).Should().BeTrue();
        (await repo.ExistsAsync("CB", "2.0", null, "nope.png")).Should().BeFalse();
    }

    [Fact]
    public async Task SearchSubmissionsAsync_WithoutFilter_ShouldReturnAllNewestFirst()
    {
        await using var db = TestDb.Create();
        var (repo, _, _, subs) = await SeedAsync(db);

        var result = await repo.SearchSubmissionsAsync(new SubmissionFilter());

        result.Select(s => s.Id).Should().Equal(subs[1].Id, subs[0].Id, subs[2].Id);
    }

    public static IEnumerable<object[]> Filters()
    {
        yield return new object[] { "group", new Func<Group, Group, SubmissionFilter>((w, _) => new SubmissionFilter { GroupId = w.Id }), new[] { "kampvuur.jpg" } };
        yield return new object[] { "assignment", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { AssignmentNumber = 1 }), new[] { "kampvuur.jpg", "knopen.mp4" } };
        yield return new object[] { "status", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { Status = SubmissionStatus.Pending }), new[] { "toren.png" } };
        yield return new object[] { "reviewer", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { Reviewer = " sara " }), new[] { "kampvuur.jpg" } };
        yield return new object[] { "from", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { FromDate = BaseTime }), new[] { "toren.png", "kampvuur.jpg" } };
        yield return new object[] { "to", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { ToDate = BaseTime }), new[] { "kampvuur.jpg", "knopen.mp4" } };
        yield return new object[] { "search file", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { SearchTerm = "TOREN" }), new[] { "toren.png" } };
        yield return new object[] { "search scout", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { SearchTerm = "daan" }), new[] { "knopen.mp4" } };
        yield return new object[] { "search text", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { SearchTerm = "vuur" }), new[] { "kampvuur.jpg" } };
        yield return new object[] { "search group", new Func<Group, Group, SubmissionFilter>((_, _) => new SubmissionFilter { SearchTerm = "bosuilen" }), new[] { "toren.png", "knopen.mp4" } };
    }

    [Theory]
    [MemberData(nameof(Filters))]
    public async Task SearchSubmissionsAsync_ShouldApplyFilter(string name, Func<Group, Group, SubmissionFilter> filter, string[] expectedFiles)
    {
        await using var db = TestDb.Create();
        var (repo, wolves, owls, _) = await SeedAsync(db);

        var result = await repo.SearchSubmissionsAsync(filter(wolves, owls));

        result.Select(s => s.FileName).Should().Equal(expectedFiles, because: name);
    }

    [Fact]
    public async Task UpdateAsync_DetachedSubmission_ShouldAttachAndSave()
    {
        await using var db = TestDb.Create();
        var (repo, _, _, subs) = await SeedAsync(db);
        var detached = new Submission
        {
            Id = subs[1].Id, GroupId = subs[1].GroupId, AssignmentNumber = 2, Status = SubmissionStatus.Awesome,
            SlackChannelId = "CB", SlackMessageId = "2.0", FileName = "toren.png", Submitter = "Emma", CreatedAt = subs[1].CreatedAt
        };

        await repo.UpdateAsync(detached);
        db.ChangeTracker.Clear();

        (await repo.GetByIdAsync(subs[1].Id))!.Status.Should().Be(SubmissionStatus.Awesome);
    }

    [Fact]
    public async Task AddAsync_ShouldPersist()
    {
        await using var db = TestDb.Create();
        var (repo, wolves, _, _) = await SeedAsync(db);
        var submission = new Submission { GroupId = wolves.Id, SlackChannelId = "CW", SlackMessageId = "4.0", FileName = "new.jpg" };

        await repo.AddAsync(submission);

        (await repo.ExistsAsync("CW", "4.0", null, "new.jpg")).Should().BeTrue();
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldCountRecentActivityPerDay()
    {
        await using var db = TestDb.Create();
        var group = new Group { Name = "G", SlackChannelId = "C" };
        db.Groups.Add(group);
        db.Submissions.AddRange(
            new Submission { GroupId = group.Id, CreatedAt = DateTimeOffset.UtcNow },
            new Submission { GroupId = group.Id, CreatedAt = DateTimeOffset.UtcNow },
            new Submission { GroupId = group.Id, CreatedAt = DateTimeOffset.UtcNow.AddDays(-30) });
        await db.SaveChangesAsync();

        var stats = await new SubmissionRepository(db).GetStatisticsAsync();

        stats.RecentActivity.Should().HaveCount(7);
        stats.RecentActivity[^1].Count.Should().Be(2);
        stats.RecentActivity.Sum(d => d.Count).Should().Be(2);
    }
}
