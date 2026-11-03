using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ScoutingAutomation.Infrastructure.Announcements;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// A theme round from opening to result, on a fake clock against a fake Slack API.
/// </summary>
public class ThemeRoundsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string ZeeAv = "C11ZEEAAV";
    private const string BosAv = "C12BOSAV";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new();
    private readonly FakeTimeProvider _clock;

    public ThemeRoundsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        // Starts a few minutes back: the webhook checks Slack's signature time against this clock, so it may not drift
        // more than a few minutes from the real time while the round runs
        var now = DateTimeOffset.UtcNow.AddMinutes(-4);
        _clock = new FakeTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero));
    }

    /// <param name="secondAfter">When set, a second round of the same length starts this long after the first ends.</param>
    private TestApp StartApp(DateTimeOffset roundStart, TimeSpan? length = null, TimeSpan? secondAfter = null)
    {
        var roundLength = length ?? TimeSpan.FromMinutes(6);
        var config = new Dictionary<string, string?>
        {
            ["Scouting:Adventurers:Enabled"] = "true",
            ["Scouting:Groups:0:AdventurersSlackChannelId"] = ZeeAv,
            ["Scouting:Groups:1:AdventurersSlackChannelId"] = BosAv,
            ["Scouting:Adventurers:Rounds:0:Number"] = "1",
            ["Scouting:Adventurers:Rounds:0:Start"] = roundStart.ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:End"] = roundStart.Add(roundLength).ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:Themes:0"] = "1",
            ["Scouting:Adventurers:Rounds:0:Message"] = "Maak een foto van een bijzondere brievenbus."
        };
        if (secondAfter is { } gap)
        {
            var secondStart = roundStart + roundLength + gap;
            config["Scouting:Adventurers:Rounds:1:Number"] = "2";
            config["Scouting:Adventurers:Rounds:1:Start"] = secondStart.ToGameTime().DateTime.ToString("s");
            config["Scouting:Adventurers:Rounds:1:End"] = secondStart.Add(roundLength).ToGameTime().DateTime.ToString("s");
            config["Scouting:Adventurers:Rounds:1:Themes:0"] = "2";
            config["Scouting:Adventurers:Rounds:1:Message"] = "Maak een foto van een rode deur.";
        }

        var app = TestApp.Live(_factory, _slack, config, services => services.AddSingleton<TimeProvider>(_clock));
        _ = app.Factory.Services;
        return app;
    }

    private IEnumerable<FakeSlackApi.Call> PostsIn(string channel) => _slack.Posts.Where(p => p["channel"] == channel);

    [Fact]
    public async Task Round_ShouldOpenCloseAndPostTheResultOnceEverythingIsReviewed()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start);
        using var client = app.CreateClient();
        await Task.Delay(200);
        _slack.Posts.Should().BeEmpty("the round has not started");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening in both Avonturiers channels");
        PostsIn(ZeeAv).Single()["text"].Should().Contain("Ronde 1 is begonnen").And.Contain("half-uur opdracht 1").And.Contain("brievenbus");
        PostsIn(BosAv).Should().ContainSingle();

        // Posted during the round: the Slack time counts, not when the jury looks
        var zee = SlackEvents.Ts(_clock.GetUtcNow().AddMinutes(2));
        var bos = SlackEvents.Ts(_clock.GetUtcNow().AddMinutes(3));
        await app.SendEventAsync(client, SlackEvents.Message(ZeeAv, zee, "Thema 1", SlackEvents.File("brievenbus.jpg")));
        await app.SendEventAsync(client, SlackEvents.Message(BosAv, bos, "T1", SlackEvents.File("bus.jpg")));
        await app.SendEventAsync(client, SlackEvents.Reaction(ZeeAv, zee, "thumbsup"));

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count == 4, "the closing in both channels");
        PostsIn(ZeeAv).Last()["text"].Should().Contain("Ronde 1 is gesloten");

        // One submission still waits for the jury: the result is provisional and not posted yet
        await Task.Delay(300);
        _slack.Posts.Should().HaveCount(4);

        await app.SendEventAsync(client, SlackEvents.Reaction(BosAv, bos, "x"));
        await _slack.WaitForAsync(s => s.Posts.Count == 6, "the result in both channels");
        PostsIn(BosAv).Last()["text"].Should().Be(":trophy: *Uitslag ronde 1:* 1. De Zeearenden (1)");

        var runtime = app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.AdventurersPostedAt(ThemeRoundAnnouncer.Key(1, RoundMessage.Result), ZeeAv).Should().NotBeNull();

        // Saved: a manual post without resend skips the channels that have it
        var again = await app.Factory.Services.GetRequiredService<ThemeRoundAnnouncer>().PostAsync(1, RoundMessage.Open, resend: false);
        again.Should().OnlyContain(r => r.Skipped);
        _slack.Posts.Should().HaveCount(6);
    }

    [Fact]
    public async Task AdjacentRounds_ShouldCloseAndOpenInOneMessage()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start, secondAfter: TimeSpan.Zero);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening of round 1 in both channels");

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("Ronde 2 is begonnen")) == 2, "the handover in both channels");
        await Task.Delay(300);

        foreach (var channel in new[] { ZeeAv, BosAv })
        {
            PostsIn(channel).Where(p => p["text"].Contains("gesloten") || p["text"].Contains("Ronde 2"))
                .Should().ContainSingle("the closing and the next opening go out together")
                .Which["text"].Should().Contain("Ronde 1 is gesloten").And.Contain("Ronde 2 is begonnen").And.Contain("rode deur");
        }

        var runtime = app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.AdventurersPostedAt(ThemeRoundAnnouncer.Key(1, RoundMessage.Close), ZeeAv).Should().NotBeNull();
        runtime.AdventurersPostedAt(ThemeRoundAnnouncer.Key(2, RoundMessage.Open), ZeeAv).Should().NotBeNull();
    }

    [Fact]
    public async Task AdjacentRounds_ShouldOnlySendWhatAChannelDidNotGetYet()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start, secondAfter: TimeSpan.Zero);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening of round 1 in both channels");

        // An admin already closed round 1 by hand in one channel
        var runtime = app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.MarkAdventurersPosted(ThemeRoundAnnouncer.Key(1, RoundMessage.Close), ZeeAv, _clock.GetUtcNow());

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("Ronde 2 is begonnen")) == 2, "the opening of round 2 in both channels");
        await Task.Delay(300);

        PostsIn(ZeeAv).Should().ContainSingle(p => p["text"].Contains("Ronde 2 is begonnen"))
            .Which["text"].Should().NotContain("gesloten");
        PostsIn(BosAv).Should().ContainSingle(p => p["text"].Contains("Ronde 2 is begonnen"))
            .Which["text"].Should().Contain("Ronde 1 is gesloten");
    }

    [Fact]
    public async Task AdjacentRounds_ShouldUseTheHandoverTextSetInTheApp()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start, secondAfter: TimeSpan.Zero);
        app.Factory.Services.GetRequiredService<RuntimeSettingsService>()
            .SetText(SlackText.RoundHandover, "Klaar met {previousThemes} (ronde {previousRound}), nu {themes} tot {end}. {message}");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening of round 1 in both channels");

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].StartsWith("Klaar met")) == 2, "the handover in both channels");
        await Task.Delay(300);

        var end = start.AddMinutes(12).ToGameTime().ToString("HH:mm");
        PostsIn(ZeeAv).Single(p => p["text"].StartsWith("Klaar met"))["text"]
            .Should().Be($"Klaar met half-uur opdracht 1 (ronde 1), nu half-uur opdracht 2 tot {end}. Maak een foto van een rode deur.");
        _slack.Posts.Should().NotContain(p => p["text"].Contains("gesloten") || p["text"].Contains("Ronde 2 is begonnen"));
    }

    [Fact]
    public async Task AdjacentRoundsWithoutHandoverText_ShouldCloseAndOpenSeparately()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start, secondAfter: TimeSpan.Zero);
        app.Factory.Services.GetRequiredService<RuntimeSettingsService>().SetText(SlackText.RoundHandover, "");
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening of round 1 in both channels");

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("Ronde 2 is begonnen")) == 2, "the opening of round 2 in both channels");
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("Ronde 1 is gesloten")) == 2, "the closing of round 1 in both channels");

        _slack.Posts.Should().NotContain(p => p["text"].Contains("gesloten") && p["text"].Contains("begonnen"));
    }

    [Fact]
    public async Task RoundsWithAGap_ShouldCloseAndOpenSeparately()
    {
        var start = _clock.GetUtcNow().AddMinutes(1);
        await using var app = StartApp(start, secondAfter: TimeSpan.FromMinutes(1));
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count == 2, "the opening of round 1 in both channels");

        _clock.Advance(TimeSpan.FromMinutes(6));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("gesloten")) == 2, "the closing of round 1 in both channels");
        _slack.Posts.Should().NotContain(p => p["text"].Contains("Ronde 2"), "round 2 has not started");

        _clock.Advance(TimeSpan.FromMinutes(1));
        await _slack.WaitForAsync(s => s.Posts.Count(p => p["text"].Contains("Ronde 2 is begonnen")) == 2, "the opening of round 2 in both channels");
        _slack.Posts.Where(p => p["text"].Contains("Ronde 2 is begonnen")).Should().NotContain(p => p["text"].Contains("gesloten"));
    }

    [Fact]
    public async Task RoundLongPast_ShouldNotBePostedByItself()
    {
        await using var app = StartApp(_clock.GetUtcNow().AddHours(-3));
        await Task.Delay(500);

        _slack.Posts.Should().BeEmpty("a start of the app long after a round must not post old rounds");
    }
}
