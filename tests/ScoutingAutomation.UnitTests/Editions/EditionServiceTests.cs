using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Editions;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Editions;

public class EditionServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _database = $"Editions_{Guid.NewGuid():N}";
    private readonly ScoutingDbContext _db;
    private readonly AccountsDbContext _accounts = new(new DbContextOptionsBuilder<AccountsDbContext>().UseInMemoryDatabase($"Accounts_{Guid.NewGuid():N}").Options);
    private readonly ActiveEdition _active = new();
    private readonly SlackProcessingGate _gate = new();
    private readonly ISubmissionChangeNotifier _changes = Substitute.For<ISubmissionChangeNotifier>();
    private readonly AccountChangeNotifier _accountChanges = new();
    private readonly LocalFileStorageService _storage;
    private readonly Edition _current;
    private readonly Group _wolves;

    public EditionServiceTests()
    {
        _db = TestDb.Create(_database);
        _storage = new LocalFileStorageService(Options.Create(new ScoutingSettings { StorageRoot = _temp.Path }),
            NullLogger<LocalFileStorageService>.Instance, _active);
        _current = new Edition { Name = "DU 2026", Year = 2026, Status = EditionStatus.Active };
        _db.Editions.Add(_current);
        _wolves = new Group { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1", AdventurersSlackChannelId = "CA1" };
        _db.Groups.Add(_wolves);
        _db.Assignments.Add(new Assignment { Code = "1", StandardPoints = 2, AwesomePoints = 4 });
        _db.SaveChanges();
        _active.Set(_current);
    }

    private EditionService Service() => Service(_db);

    private EditionService Service(ScoutingDbContext db) => new(db, _accounts, _active, _gate, _storage, new SubmissionRepository(db), _changes,
        _accountChanges, TimeProvider.System, NullLogger<EditionService>.Instance);

    private async Task<Edition> NextAsync(bool copyGroups = true)
    {
        var (edition, error) = await Service().CreateAsync("DU 2027", 2027, copyGroups);
        error.Should().BeNull();
        return edition!;
    }

    private void AddSubmission(Group group, SubmissionStatus status, string code = "1")
    {
        _db.Submissions.Add(new Submission
        {
            GroupId = group.Id, AssignmentCode = code, SlackChannelId = group.SlackChannelId, SlackMessageId = Guid.NewGuid().ToString("N"),
            Submitter = "U1", FileName = "a.jpg", StoragePath = Path.Combine(_temp.Path, "a.jpg"), Status = status
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Create_ShouldCopyAssignmentsAndGroupsWithTheirTeams()
    {
        var next = await NextAsync();

        next.Status.Should().Be(EditionStatus.Draft);
        next.StorageFolder.Should().Be("_edities/du-2027");
        var groups = await Service().GetGroupsAsync(next.Id);
        groups.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Name = "De Wolven", TeamId = _wolves.TeamId, SlackChannelId = "C1", AdventurersSlackChannelId = "CA1" });
        groups[0].Id.Should().NotBe(_wolves.Id);
        groups[0].MapToken.Should().NotBe(_wolves.MapToken);
        (await _db.Assignments.IgnoreQueryFilters().CountAsync(a => a.EditionId == next.Id && a.Code == "1")).Should().Be(1);
        (await _db.Groups.CountAsync()).Should().Be(1, "the active edition still has only its own group");
    }

    [Theory]
    [InlineData("", 2027, "naam")]
    [InlineData("DU 2026", 2027, "al een editie")]
    [InlineData("DU 2027", 1900, "jaar")]
    public async Task Create_Invalid_ShouldSayWhy(string name, int year, string expected)
    {
        var (edition, error) = await Service().CreateAsync(name, year, copyGroups: false);

        edition.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public async Task SaveGroup_ShouldRefuseAChannelOrTeamTwiceInOneEdition()
    {
        var next = await NextAsync();
        var service = Service();

        (await service.SaveGroupAsync(next.Id, new GroupInput(null, null, "De Bosuilen", null, "C1", null, null))).Should().Contain("kanaal");
        (await service.SaveGroupAsync(next.Id, new GroupInput(null, null, "De Bosuilen", null, "CA1", null, null))).Should().Contain("kanaal");
        (await service.SaveGroupAsync(next.Id, new GroupInput(null, _wolves.TeamId, "Wolven", null, "C9", null, null))).Should().Contain("team");
        (await service.SaveGroupAsync(next.Id, new GroupInput(null, null, "de wolven", null, "C9", null, null))).Should().Contain("naam");
        (await service.SaveGroupAsync(next.Id, new GroupInput(null, null, "De Bosuilen", "DBU", " C2 ", " ", null))).Should().BeNull();

        var bosuilen = (await service.GetGroupsAsync(next.Id)).Single(g => g.Name == "De Bosuilen");
        bosuilen.SlackChannelId.Should().Be("C2");
        bosuilen.AdventurersSlackChannelId.Should().BeNull();
        bosuilen.TeamId.Should().Be(bosuilen.Id, "a new group gets its own team");
        (await _db.Groups.CountAsync()).Should().Be(1, "a group of a later edition is not in the game being played");
    }

    [Fact]
    public async Task SaveGroup_InTheActiveEdition_ShouldMakeItsFoldersAndChangeTheGroup()
    {
        var result = await Service().SaveGroupAsync(_current.Id, new GroupInput(_wolves.Id, _wolves.TeamId, "De Wolven", "WLV", "C1", null, "Welpen"));

        result.Should().BeNull();
        (await _db.Groups.AsNoTracking().SingleAsync()).Should().BeEquivalentTo(new { Abbreviation = "WLV", AdventurersSlackChannelId = (string?)null, Description = "Welpen" });
        Directory.Exists(Path.Combine(_temp.Path, "de-wolven", "approved")).Should().BeTrue();
        _changes.Received().NotifyChanged();
        _gate.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveGroup_WithOpenSubmissions_ShouldBeRefused()
    {
        AddSubmission(_wolves, SubmissionStatus.Pending);

        var result = await Service().RemoveGroupAsync(_wolves.Id);

        result.Should().Contain("1 inzending die niet beoordeeld is");
        (await _db.Groups.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RemoveGroup_WithReviewedSubmissions_ShouldBeRefusedToo()
    {
        AddSubmission(_wolves, SubmissionStatus.Approved);

        (await Service().RemoveGroupAsync(_wolves.Id)).Should().Contain("al inzendingen");
    }

    [Fact]
    public async Task RemoveGroup_WithoutSubmissions_ShouldTakeItOutOfTheEditionOnly()
    {
        var next = await NextAsync();
        var copy = (await Service().GetGroupsAsync(next.Id)).Single();

        (await Service().RemoveGroupAsync(copy.Id)).Should().BeNull();

        (await Service().GetGroupsAsync(next.Id)).Should().BeEmpty();
        (await _db.Groups.CountAsync()).Should().Be(1, "the group stays in the active edition");
        (await _db.Teams.CountAsync()).Should().Be(1, "the team stays");
    }

    [Fact]
    public async Task Activate_ShouldArchiveTheCurrentEditionWithItsStandingAndSwitch()
    {
        AddSubmission(_wolves, SubmissionStatus.Approved);
        _accounts.Users.Add(new AppUser { UserName = "groep@du.test", Email = "groep@du.test", GroupId = _wolves.TeamId, SecurityStamp = "oud" });
        _accounts.Users.Add(new AppUser { UserName = "jury@du.test", Email = "jury@du.test", SecurityStamp = "jury" });
        await _accounts.SaveChangesAsync();
        var loggedOut = new List<Guid>();
        _accountChanges.Changed += loggedOut.Add;
        var next = await NextAsync();

        (await Service().ActivateAsync(next.Id)).Should().BeNull();

        var old = await _db.Editions.AsNoTracking().SingleAsync(e => e.Id == _current.Id);
        old.Status.Should().Be(EditionStatus.Archived);
        old.ArchivedAt.Should().NotBeNull();
        EditionService.ReadStanding(old).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { TeamId = _wolves.TeamId, GroupId = _wolves.Id, GroupName = "De Wolven", Rank = 1, Score = 2 });
        _active.Current.Id.Should().Be(next.Id);
        _active.Current.StorageFolder.Should().Be("_edities/du-2027");

        var groups = await _db.Groups.AsNoTracking().ToListAsync();
        groups.Should().ContainSingle().Which.EditionId.Should().Be(next.Id);
        (await _db.Submissions.CountAsync()).Should().Be(0, "the new edition starts without submissions");
        Directory.Exists(Path.Combine(_temp.Path, "_edities", "du-2027", "de-wolven")).Should().BeTrue("files of the new edition get their own folder");

        var users = await _accounts.Users.AsNoTracking().ToListAsync();
        users.Single(u => u.Email == "groep@du.test").SecurityStamp.Should().NotBe("oud", "the group logs in again to get its new group");
        users.Single(u => u.Email == "jury@du.test").SecurityStamp.Should().Be("jury");
        loggedOut.Should().ContainSingle();
        _gate.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task Activate_OnlyADraft()
    {
        (await Service().ActivateAsync(_current.Id)).Should().Contain("nog niet gespeeld");
    }

    [Fact]
    public async Task Activate_WhileARebuildRuns_ShouldWait()
    {
        var next = await NextAsync();
        using (await _gate.CloseAsync())
        {
            (await Service().ActivateAsync(next.Id)).Should().Contain("opnieuw opgebouwd");
        }

        (await _db.Editions.AsNoTracking().SingleAsync(e => e.Id == next.Id)).Status.Should().Be(EditionStatus.Draft);
    }

    [Fact]
    public async Task Archived_ShouldNoLongerChange()
    {
        var next = await NextAsync();
        await Service().ActivateAsync(next.Id);

        (await Service().SaveGroupAsync(_current.Id, new GroupInput(_wolves.Id, null, "Andere naam", null, "C1", null, null))).Should().Contain("gearchiveerd");
        (await Service().RemoveGroupAsync(_wolves.Id)).Should().Contain("gearchiveerd");
        (await Service().DeleteAsync(_current.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_ShouldRemoveADraftWithItsGroupsAndAssignments()
    {
        var next = await NextAsync();

        (await Service().DeleteAsync(next.Id)).Should().BeNull();

        (await _db.Editions.CountAsync()).Should().Be(1);
        (await _db.Groups.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await _db.Assignments.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await Service().DeleteAsync(_current.Id)).Should().Contain("nog niet gespeeld");
    }

    [Fact]
    public async Task DeleteAndStartAtOnce_ShouldNeverLeaveTheGameWithoutAnActiveEdition()
    {
        var next = await NextAsync();
        await using var first = TestDb.Create(_database);
        await using var second = TestDb.Create(_database);

        var results = await Task.WhenAll(
            Task.Run(() => Service(first).ActivateAsync(next.Id)),
            Task.Run(() => Service(second).DeleteAsync(next.Id)));

        results.Count(r => r == null).Should().Be(1, "the second one sees what the first did");
        await using var check = TestDb.Create(_database);
        (await check.Editions.CountAsync(e => e.Status == EditionStatus.Active)).Should().Be(1);
    }

    [Fact]
    public async Task TeamHistory_ShouldListTheArchivedEditionsOfTheTeamOnly()
    {
        AddSubmission(_wolves, SubmissionStatus.Approved);
        var next = await NextAsync();
        await Service().ActivateAsync(next.Id);

        var history = await Service().GetTeamHistoryAsync(_wolves.TeamId);

        history.Should().ContainSingle();
        history[0].Edition.Name.Should().Be("DU 2026");
        history[0].Standing!.Rank.Should().Be(1);
        (await Service().GetTeamHistoryAsync(Guid.NewGuid())).Should().BeEmpty();
    }

    public void Dispose()
    {
        _db.Dispose();
        _accounts.Dispose();
        _temp.Dispose();
    }
}
