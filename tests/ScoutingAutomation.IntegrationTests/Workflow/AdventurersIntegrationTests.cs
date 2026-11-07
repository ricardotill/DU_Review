using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// Check-ins and themes in the Avonturiers channels, end to end in live mode against a fake Slack API.
/// </summary>
public class AdventurersIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Toeristen = "C01ZEEARENDEN";
    private const string Avonturiers = "C11ZEEAAV";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new();

    public AdventurersIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private TestApp StartApp(Dictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Scouting:Adventurers:Enabled"] = "true",
            ["Scouting:Adventurers:SuperLocations:Enabled"] = "false",
            ["Scouting:Groups:0:AdventurersSlackChannelId"] = Avonturiers,
            ["Scouting:Groups:1:AdventurersSlackChannelId"] = "C12BOSAV"
        };
        foreach (var (key, value) in extra ?? new()) settings[key] = value;
        return TestApp.Live(_factory, _slack, settings);
    }

    /// <summary>
    /// 150 of the 160 assignments open at the start and 10 locations, so each counted location opens exactly one more
    /// (151, 152, ...). Locations 1 through 5 are open at the start.
    /// </summary>
    private static readonly Dictionary<string, string?> OneAssignmentPerLocation = new()
    {
        ["Scouting:Adventurers:InitialOpenAssignments"] = "150",
        ["Scouting:Adventurers:InitialOpenLocations"] = "5",
        // Off, so the counts do not depend on the super locations in the shipped appsettings
        ["Scouting:Adventurers:SuperLocations:Enabled"] = "false"
    };

    private static Task AddLocationsAsync(TestApp app, int count) => app.GetAsync(async sp =>
    {
        var csv = "nummer;naam;lat;lon\n" + string.Join("\n", Enumerable.Range(1, count).Select(n => $"{n};Plek {n};51.{n};5.{n}"));
        var result = await sp.GetRequiredService<LocationService>().ImportAsync("locaties.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)));
        result.Problems.Should().BeEmpty();
        return result;
    });

    private static Task<UnlockState?> UnlocksOf(TestApp app, string channel) => app.GetAsync(async sp =>
    {
        var group = await sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(channel);
        return await sp.GetRequiredService<AssignmentUnlockService>().ForGroupAsync(group!.Id);
    });

    private static Task<IReadOnlyList<Submission>> Submissions(TestApp app, string channel, string ts) =>
        app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(channel, ts));

    [Fact]
    public async Task CheckIn_ShouldBeStoredAsLocationAndOpenAnAssignmentForTheToeristen()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();
        var ts = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, ts, "Locatie 3, iedereen staat erop!", SlackEvents.File("molen.jpg")));

        var checkIn = (await Submissions(app, Avonturiers, ts)).Single();
        checkIn.Kind.Should().Be(SubmissionKind.CheckIn);
        checkIn.AssignmentCode.Should().Be("3");
        checkIn.StoragePath.Replace('\\', '/').Should().Contain("/de-zeearenden/avonturiers/").And.Contain("DZA L3");

        // Awesome counts as approved for a check-in, and the Toeristen hear about it in their own channel
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, ts, "heart"));
        (await Submissions(app, Avonturiers, ts)).Single().Status.Should().Be(SubmissionStatus.Approved);
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        _slack.Posts.Single(p => p["channel"] == Toeristen)["text"].Should().Contain("opdracht 151 doen").And.Contain("151 van de 160").And.NotContain("{");

        // A second approval of the same location opens nothing more
        var again = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, again, "L3 nog een keer", SlackEvents.File("molen2.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, again, "thumbsup"));
        await Task.Delay(300);
        _slack.Posts.Count(p => p["channel"] == Toeristen).Should().Be(1);

        // Taking both approvals back posts a correction
        await app.SendEventAsync(client, SlackEvents.ReactionRemoved(Avonturiers, ts, "heart"));
        await app.SendEventAsync(client, SlackEvents.ReactionRemoved(Avonturiers, again, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["channel"] == Toeristen) == 2, "the correction");
        _slack.Posts.Last(p => p["channel"] == Toeristen)["text"].Should().StartWith(":leftwards_arrow_with_hook: Correctie");

        (await UnlocksOf(app, Toeristen))!.OpenCount.Should().Be(150);
    }

    [Fact]
    public async Task CheckInAtASuperLocation_ShouldOpenTwiceAsMany()
    {
        // 20 to open over 9 locations, location 3 super: L = 10, a normal location opens 2, the super one 4
        await using var app = StartApp(new()
        {
            ["Scouting:Adventurers:InitialOpenAssignments"] = "140",
            ["Scouting:Adventurers:InitialOpenLocations"] = "9",
            ["Scouting:Adventurers:SuperLocations:Enabled"] = "true",
            ["Scouting:Adventurers:SuperLocations:Numbers:0"] = "3"
        });
        await AddLocationsAsync(app, 9);
        using var client = app.CreateClient();
        var ts = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, ts, "Locatie 3", SlackEvents.File("super.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, ts, "thumbsup"));

        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        _slack.Posts.Single(p => p["channel"] == Toeristen)["text"].Should().Contain("141, 142, 143 en 144").And.Contain("144 van de 160");
        var state = (await UnlocksOf(app, Toeristen))!;
        state.OpenCount.Should().Be(144);
        state.NextLocationOpens.Should().Be(2);
        state.NextSuperLocationOpens.Should().Be(0, "the only super location is visited");
    }

    [Fact]
    public async Task OpdrachtInAvonturiersChannel_ShouldBeATheme_AndOutsideItsRoundItIsStoredButNotScored()
    {
        // Round 1 with theme 1 runs now; theme 2 belongs to a later round
        var now = DateTimeOffset.UtcNow.ToGameTime().DateTime;
        await using var app = StartApp(new()
        {
            ["Scouting:Adventurers:Rounds:0:Number"] = "1",
            ["Scouting:Adventurers:Rounds:0:Start"] = now.AddMinutes(-10).ToString("s"),
            ["Scouting:Adventurers:Rounds:0:Themes:0"] = "1",
            ["Scouting:Adventurers:Rounds:1:Number"] = "2",
            ["Scouting:Adventurers:Rounds:1:Start"] = now.AddMinutes(30).ToString("s"),
            ["Scouting:Adventurers:Rounds:1:Themes:0"] = "2"
        });
        using var client = app.CreateClient();

        var inRound = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, inRound, "Opdracht 1", SlackEvents.File("thema1.jpg")));
        var tooEarly = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, tooEarly, "T2", SlackEvents.File("thema2.jpg")));

        (await Submissions(app, Avonturiers, inRound)).Single().Kind.Should().Be(SubmissionKind.Theme);
        _slack.ThreadReplies(inRound).Should().BeEmpty();

        var early = (await Submissions(app, Avonturiers, tooEarly)).Single();
        early.Kind.Should().Be(SubmissionKind.Theme);
        early.AssignmentCode.Should().Be("2");
        _slack.Calls("reactions.add").Should().Contain(c => c["timestamp"] == tooEarly && c["name"] == "hourglass");
        _slack.ThreadReplies(tooEarly).Should().ContainSingle().Which["text"].Should().Contain("ronde 2");

        // In the Toeristen channel the same words stay an assignment
        var toeristen = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, toeristen, "Opdracht 1", SlackEvents.File("toerist.jpg")));
        (await Submissions(app, Toeristen, toeristen)).Single().Kind.Should().Be(SubmissionKind.Assignment);

        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, inRound, "thumbsup"));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, tooEarly, "thumbsup"));
        var overview = await app.GetAsync(sp => sp.GetRequiredService<AdventurersScoreService>().GetOverviewAsync());
        overview.Standing.Rounds[0].Teams.Should().ContainSingle().Which.Approved.Should().Be(1, "theme 2 was posted outside its round");
        overview.Standing.Rounds[1].Teams.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckInDuringARound_ShouldOpenTheAssignmentOnlyAtTheEndOfTheRound()
    {
        // A few minutes back: the webhook checks Slack's signature time against this clock
        var real = DateTimeOffset.UtcNow.AddMinutes(-4);
        var clock = new FakeTimeProvider(new DateTimeOffset(real.Year, real.Month, real.Day, real.Hour, real.Minute, 0, TimeSpan.Zero));
        var start = clock.GetUtcNow().AddMinutes(-1);
        var settings = new Dictionary<string, string?>(OneAssignmentPerLocation)
        {
            ["Scouting:Adventurers:Enabled"] = "true",
            ["Scouting:Groups:0:AdventurersSlackChannelId"] = Avonturiers,
            ["Scouting:Adventurers:Rounds:0:Number"] = "1",
            ["Scouting:Adventurers:Rounds:0:Start"] = start.ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:End"] = start.AddMinutes(6).ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:Themes:0"] = "1"
        };
        await using var app = TestApp.Live(_factory, _slack, settings, services => services.AddSingleton<TimeProvider>(clock));
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();

        var checkIn = SlackEvents.Ts(clock.GetUtcNow().AddMinutes(1));
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "Locatie 3", SlackEvents.File("plek.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await Task.Delay(300);
        _slack.Posts.Should().NotContain(p => p["channel"] == Toeristen, "the round is still running");
        var waiting = (await UnlocksOf(app, Toeristen))!;
        waiting.OpenCount.Should().Be(150);
        waiting.WaitingLocations.Should().Equal("3");
        waiting.NextReleaseAt.Should().Be(start.AddMinutes(6));

        var locked = SlackEvents.Ts(clock.GetUtcNow().AddMinutes(2));
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, locked, "Opdracht 151", SlackEvents.File("dicht.jpg")));
        (await Submissions(app, Toeristen, locked)).Should().BeEmpty();

        clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen && p["text"].Contains("opdracht 151 doen")), "the unlock notice at the end of the round");
        (await UnlocksOf(app, Toeristen))!.OpenCount.Should().Be(151);

        var unlocked = SlackEvents.Ts(clock.GetUtcNow());
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, unlocked, "Opdracht 151", SlackEvents.File("open.jpg")));
        (await Submissions(app, Toeristen, unlocked)).Should().ContainSingle();
    }

    [Fact]
    public async Task LockedAssignment_ShouldGetALockUntilTheAvonturiersOpenIt()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();

        // Open from the start: no lock
        var open = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, open, "Opdracht 150", SlackEvents.File("open.jpg")));
        (await Submissions(app, Toeristen, open)).Should().ContainSingle();

        var locked = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, locked, "Opdracht 151", SlackEvents.File("dicht.jpg")));
        (await Submissions(app, Toeristen, locked)).Should().BeEmpty();
        _slack.Calls("reactions.add").Should().Contain(c => c["timestamp"] == locked && c["name"] == "lock");

        // An approved check-in at an open location opens the next assignment
        var checkIn = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "#3", SlackEvents.File("plek.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");

        var unlocked = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, unlocked, "Opdracht 151", SlackEvents.File("open2.jpg")));
        (await Submissions(app, Toeristen, unlocked)).Should().ContainSingle();

        var stillLocked = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, stillLocked, "Opdracht 152", SlackEvents.File("dicht2.jpg")));
        (await Submissions(app, Toeristen, stillLocked)).Should().BeEmpty();
    }

    [Fact]
    public async Task CheckInAtClosedLocation_ShouldBeStoredButCountOnceTheOrganisationOpensIt()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();

        var ts = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, ts, "Locatie 8", SlackEvents.File("ver.jpg")));

        (await Submissions(app, Avonturiers, ts)).Should().ContainSingle("it is stored, to count later");
        _slack.Calls("reactions.add").Should().Contain(c => c["timestamp"] == ts && c["name"] == "construction");
        _slack.ThreadReplies(ts).Should().ContainSingle().Which["text"].Should().Contain("nog niet open");

        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, ts, "thumbsup"));
        await Task.Delay(300);
        _slack.Posts.Should().NotContain(p => p["channel"] == Toeristen, "a closed location opens nothing");
        (await UnlocksOf(app, Toeristen))!.OpenCount.Should().Be(150);

        // The organisation opens locations 1 through 8 for the group: the check-in counts now
        await app.GetAsync(async sp =>
        {
            var group = await sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Toeristen);
            return await sp.GetRequiredService<LocationService>().SetOpenLocationsAsync(group!.Id, 8);
        });

        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        _slack.Posts.Single(p => p["channel"] == Toeristen)["text"].Should().Contain("opdracht 151 doen");
        (await UnlocksOf(app, Toeristen))!.CountedLocations.Should().Equal("8");
    }

    [Fact]
    public async Task EditToALockedAssignment_ShouldChangeNothingAndGetALock()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();
        var ts = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, ts, "Opdracht 150", SlackEvents.File("open.jpg", id: "F150")));

        await app.SendEventAsync(client, SlackEvents.Edited(Toeristen, ts, "Opdracht 150", "Opdracht 151", new[] { SlackEvents.File("open.jpg", id: "F150") }));

        (await Submissions(app, Toeristen, ts)).Single().AssignmentCode.Should().Be("150", "an edit is no way around the lock");
        _slack.Calls("reactions.add").Should().Contain(c => c["timestamp"] == ts && c["name"] == "lock");
    }

    [Fact]
    public async Task FirstLocationsAfterAnApprovedCheckIn_ShouldNotPostACorrection()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        using var client = app.CreateClient();
        // No locations yet: everything is open, and an approved check-in is no news
        var checkIn = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "Locatie 3", SlackEvents.File("plek.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await Task.Delay(300);

        await AddLocationsAsync(app, 10);

        // Location 3 is open and approved: the first count with locations is 151, told as news, not as a drop from 160
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        await Task.Delay(300);
        _slack.Posts.Where(p => p["channel"] == Toeristen).Should().ContainSingle()
            .Which["text"].Should().Contain("opdracht 151 doen").And.NotContain("Correctie");
    }

    [Fact]
    public async Task Rebuild_ShouldOnlySkipMessagesTheBotItselfLocked()
    {
        _slack.Workspace = ("T1", "https://du.slack.com");
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();
        await app.Factory.Services.GetRequiredService<ISlackCatchUpService>().Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var byBot = SlackEvents.Ts(-40);
        var byScout = SlackEvents.Ts(-30);
        _slack.History[Toeristen] = new[]
        {
            SlackEvents.HistoryMessage(byBot, "Opdracht 151", new[] { SlackEvents.File("dicht.jpg") }, new[] { SlackEvents.Reacted("lock", _slack.BotUserId) }),
            SlackEvents.HistoryMessage(byScout, "Opdracht 12", new[] { SlackEvents.File("open.jpg") }, new[] { SlackEvents.Reacted("lock", SlackEvents.Scout) })
        };
        _slack.History[Avonturiers] = Array.Empty<object>();
        var group = await app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Toeristen));
        var rebuild = app.Factory.Services.GetRequiredService<ISlackRebuildService>();

        rebuild.TryStart(DateTimeOffset.UtcNow.AddHours(-1), group!.Id).Should().BeTrue();
        await rebuild.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        rebuild.Progress.State.Should().Be(RebuildState.Completed, rebuild.Progress.Error);
        (await Submissions(app, Toeristen, byBot)).Should().BeEmpty("the bot refused it live");
        (await Submissions(app, Toeristen, byScout)).Should().ContainSingle("a scout's emoji does not drop a submission");
    }

    [Fact]
    public async Task SlackPostTriedAgain_ShouldTellTheGroupPagesOnce()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();
        var toasts = new List<AssignmentsUnlockedNotification>();
        app.Factory.Services.GetRequiredService<ISubmissionChangeNotifier>().Unlocked += n => { lock (toasts) toasts.Add(n); };
        var notifier = app.Factory.Services.GetRequiredService<UnlockNotifier>();
        _slack.Errors["chat.postMessage"] = "channel_not_found";

        var checkIn = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "Locatie 3", SlackEvents.File("plek.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await _slack.WaitForAsync(_ => notifier.Pending == 0 && toasts.Count > 0, "the first try");

        var group = await app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Toeristen));
        (await notifier.NotifyAsync(group!.Id)).Should().BeFalse("Slack still refuses");
        _slack.Errors.TryRemove("chat.postMessage", out _);
        (await notifier.NotifyAsync(group.Id)).Should().BeTrue();

        toasts.Should().ContainSingle().Which.NewCodes.Should().Equal("151");
        _slack.Posts.Last(p => p["channel"] == Toeristen)["text"].Should().Contain("opdracht 151 doen", "Slack still gets the notice once it works");
    }

    [Fact]
    public async Task WithoutLocations_EveryAssignmentIsOpen()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        using var client = app.CreateClient();

        var ts = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, ts, "Opdracht 160", SlackEvents.File("laatste.jpg")));

        (await Submissions(app, Toeristen, ts)).Should().ContainSingle("nothing can be unlocked before the locations are set up");
    }

    [Fact]
    public async Task AdventurersOff_ShouldIgnoreTheirChannel()
    {
        await using var app = StartApp(new() { ["Scouting:Adventurers:Enabled"] = "false" });
        using var client = app.CreateClient();
        var ts = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, ts, "Locatie 7", SlackEvents.File("molen.jpg")));

        (await Submissions(app, Avonturiers, ts)).Should().BeEmpty();
        _slack.Writes.Should().BeEmpty();
    }

    private static string GameTimeSetting(TimeSpan fromNow) => DateTimeOffset.UtcNow.Add(fromNow).ToGameTime().DateTime.ToString("s");

    [Fact]
    public async Task BeforeTheAvonturiersStart_TheirMessagesShouldBeIgnored_WhileTheToeristenPlay()
    {
        await using var app = StartApp(new()
        {
            ["Scouting:EventStart"] = GameTimeSetting(TimeSpan.FromHours(-1)),
            ["Scouting:Adventurers:StartAnnouncement:PostAt"] = GameTimeSetting(TimeSpan.FromHours(1))
        });
        using var client = app.CreateClient();
        var early = SlackEvents.Ts();
        var toeristen = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, early, "Locatie 3", SlackEvents.File("molen.jpg")));
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, toeristen, "Opdracht 1", SlackEvents.File("foto.jpg")));

        (await Submissions(app, Avonturiers, early)).Should().BeEmpty("the Avonturiers have not started");
        (await Submissions(app, Toeristen, toeristen)).Should().ContainSingle("the Toeristen started an hour ago");
        _slack.Writes.Should().NotContain(w => w["channel"] == Avonturiers, "a message from before the start gets no reply");
    }

    [Fact]
    public async Task AfterTheAvonturiersEnd_TheirMessagesShouldBeTooLate_WhileTheToeristenPlay()
    {
        await using var app = StartApp(new()
        {
            ["Scouting:EventEnd"] = GameTimeSetting(TimeSpan.FromHours(1)),
            ["Scouting:Adventurers:FinalAnnouncement:PostAt"] = GameTimeSetting(TimeSpan.FromMinutes(-10))
        });
        using var client = app.CreateClient();
        var late = SlackEvents.Ts();
        var toeristen = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, late, "Locatie 3", SlackEvents.File("molen.jpg")));
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, toeristen, "Opdracht 1", SlackEvents.File("foto.jpg")));

        (await Submissions(app, Avonturiers, late)).Should().BeEmpty("the Avonturiers ended ten minutes ago");
        (await Submissions(app, Toeristen, toeristen)).Should().ContainSingle("the Toeristen play for another hour");
        _slack.Calls("reactions.add").Should().Contain(r => r["channel"] == Avonturiers && r["timestamp"] == late && r["name"] == "alarm_clock");
    }

    [Fact]
    public async Task RebuildOfOneGroup_ShouldReadBothChannels_WithoutPostingNoticesAgain()
    {
        await using var app = StartApp(OneAssignmentPerLocation);
        await AddLocationsAsync(app, 10);
        using var client = app.CreateClient();
        await app.Factory.Services.GetRequiredService<ISlackCatchUpService>().Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var checkIn = SlackEvents.Ts(-60);
        var assignment = SlackEvents.Ts(-30);
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "Locatie 3", SlackEvents.File("molen.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        var writes = _slack.Writes.Count;

        _slack.History[Avonturiers] = new[]
        {
            SlackEvents.HistoryMessage(checkIn, "Locatie 3", new[] { SlackEvents.File("molen.jpg") }, new[] { SlackEvents.Reacted("thumbsup", TestApp.Reviewer) }),
            SlackEvents.HistoryMessage(SlackEvents.Ts(-20), "Locatie 4", new[] { SlackEvents.File("kerk.jpg") }, new[] { SlackEvents.Reacted("thumbsup", TestApp.Reviewer) })
        };
        _slack.History[Toeristen] = new[]
        {
            SlackEvents.HistoryMessage(assignment, "Opdracht 3", new[] { SlackEvents.File("drie.jpg") })
        };
        var group = await app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Toeristen));
        var rebuild = app.Factory.Services.GetRequiredService<ISlackRebuildService>();

        rebuild.TryStart(DateTimeOffset.UtcNow.AddHours(-1), group!.Id).Should().BeTrue();
        await rebuild.Completion.WaitAsync(TimeSpan.FromSeconds(20));

        rebuild.Progress.State.Should().Be(RebuildState.Completed, rebuild.Progress.Error);
        rebuild.Progress.ChannelProblems.Should().BeEmpty();
        rebuild.Progress.ChannelsTotal.Should().Be(2, "the group has a Toeristen and an Avonturiers channel");
        var all = await app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new() { GroupId = group.Id }));
        all.Select(s => (s.Kind, s.AssignmentCode, s.Status)).Should().BeEquivalentTo(new[]
        {
            (SubmissionKind.CheckIn, "3", SubmissionStatus.Approved),
            (SubmissionKind.CheckIn, "4", SubmissionStatus.Approved),
            (SubmissionKind.Assignment, "3", SubmissionStatus.Pending)
        });
        await Task.Delay(300);
        _slack.Writes.Count.Should().Be(writes, "a rebuild replays Slack without posting anything");
    }
}
