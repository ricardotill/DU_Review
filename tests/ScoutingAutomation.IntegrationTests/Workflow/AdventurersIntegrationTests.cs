using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack.Sync;
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
            ["Scouting:Groups:0:AdventurersSlackChannelId"] = Avonturiers,
            ["Scouting:Groups:1:AdventurersSlackChannelId"] = "C12BOSAV"
        };
        foreach (var (key, value) in extra ?? new()) settings[key] = value;
        return TestApp.Live(_factory, _slack, settings);
    }

    private static Task<IReadOnlyList<Submission>> Submissions(TestApp app, string channel, string ts) =>
        app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(channel, ts));

    [Fact]
    public async Task CheckIn_ShouldBeStoredAsLocationAndUnlockAnAssignmentForTheToeristen()
    {
        await using var app = StartApp();
        using var client = app.CreateClient();
        var ts = SlackEvents.Ts();

        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, ts, "Locatie 7, iedereen staat erop!", SlackEvents.File("molen.jpg")));

        var checkIn = (await Submissions(app, Avonturiers, ts)).Single();
        checkIn.Kind.Should().Be(SubmissionKind.CheckIn);
        checkIn.AssignmentCode.Should().Be("7");
        checkIn.StoragePath.Replace('\\', '/').Should().Contain("/de-zeearenden/avonturiers/").And.Contain("DZA L7");

        // Awesome counts as approved for a check-in, and the Toeristen hear about it in their own channel
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, ts, "heart"));
        (await Submissions(app, Avonturiers, ts)).Single().Status.Should().Be(SubmissionStatus.Approved);
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        _slack.Posts.Single(p => p["channel"] == Toeristen)["text"].Should().Contain("opdracht 1").And.NotContain("{");

        // A second approval of the same location unlocks nothing more
        var again = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, again, "L7 nog een keer", SlackEvents.File("molen2.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, again, "thumbsup"));
        await Task.Delay(300);
        _slack.Posts.Count(p => p["channel"] == Toeristen).Should().Be(1);

        // Taking both approvals back posts a correction
        await app.SendEventAsync(client, SlackEvents.ReactionRemoved(Avonturiers, ts, "heart"));
        await app.SendEventAsync(client, SlackEvents.ReactionRemoved(Avonturiers, again, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["channel"] == Toeristen) == 2, "the correction");
        _slack.Posts.Last(p => p["channel"] == Toeristen)["text"].Should().StartWith(":leftwards_arrow_with_hook: Correctie");

        var standing = await app.GetAsync(sp => sp.GetRequiredService<AdventurersScoreService>().GetOverviewAsync());
        standing.Standing.Groups.Values.Should().OnlyContain(g => g.UnlockedAssignments == 0);
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
    public async Task PoolMode_LockedAssignment_ShouldGetALockAndNotBeStored()
    {
        await using var app = StartApp(new()
        {
            ["Scouting:Adventurers:UnlockPool:0"] = "4",
            ["Scouting:Adventurers:UnlockPool:1"] = "9"
        });
        using var client = app.CreateClient();

        var locked = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, locked, "Opdracht 4", SlackEvents.File("vier.jpg")));

        (await Submissions(app, Toeristen, locked)).Should().BeEmpty();
        _slack.Calls("reactions.add").Should().Contain(c => c["timestamp"] == locked && c["name"] == "lock");

        // An approved check-in unlocks the first assignment of the pool
        var checkIn = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "#3", SlackEvents.File("plek.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        _slack.Posts.Single(p => p["channel"] == Toeristen)["text"].Should().Contain("opdracht 1 van 2").And.Contain("opdracht 4 doen");

        var unlocked = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, unlocked, "Opdracht 4", SlackEvents.File("vier2.jpg")));
        (await Submissions(app, Toeristen, unlocked)).Should().ContainSingle();

        // Not in the pool: never locked
        var free = SlackEvents.Ts();
        await app.SendEventAsync(client, SlackEvents.Message(Toeristen, free, "Opdracht 5", SlackEvents.File("vijf.jpg")));
        (await Submissions(app, Toeristen, free)).Should().ContainSingle();
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

    [Fact]
    public async Task RebuildOfOneGroup_ShouldReadBothChannels_WithoutPostingNoticesAgain()
    {
        await using var app = StartApp();
        using var client = app.CreateClient();
        await app.Factory.Services.GetRequiredService<ISlackCatchUpService>().Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var checkIn = SlackEvents.Ts(-60);
        var assignment = SlackEvents.Ts(-30);
        await app.SendEventAsync(client, SlackEvents.Message(Avonturiers, checkIn, "Locatie 7", SlackEvents.File("molen.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(Avonturiers, checkIn, "thumbsup"));
        await _slack.WaitForAsync(s => s.Posts.Any(p => p["channel"] == Toeristen), "the unlock notice");
        var writes = _slack.Writes.Count;

        _slack.History[Avonturiers] = new[]
        {
            SlackEvents.HistoryMessage(checkIn, "Locatie 7", new[] { SlackEvents.File("molen.jpg") }, new[] { SlackEvents.Reacted("thumbsup", TestApp.Reviewer) }),
            SlackEvents.HistoryMessage(SlackEvents.Ts(-20), "Locatie 8", new[] { SlackEvents.File("kerk.jpg") }, new[] { SlackEvents.Reacted("thumbsup", TestApp.Reviewer) })
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
            (SubmissionKind.CheckIn, "7", SubmissionStatus.Approved),
            (SubmissionKind.CheckIn, "8", SubmissionStatus.Approved),
            (SubmissionKind.Assignment, "3", SubmissionStatus.Pending)
        });
        await Task.Delay(300);
        _slack.Writes.Count.Should().Be(writes, "a rebuild replays Slack without posting anything");
    }
}
