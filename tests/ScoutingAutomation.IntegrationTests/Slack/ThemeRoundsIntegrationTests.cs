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

    private TestApp StartApp(DateTimeOffset roundStart, TimeSpan? length = null)
    {
        var app = TestApp.Live(_factory, _slack, new Dictionary<string, string?>
        {
            ["Scouting:Adventurers:Enabled"] = "true",
            ["Scouting:Groups:0:AdventurersSlackChannelId"] = ZeeAv,
            ["Scouting:Groups:1:AdventurersSlackChannelId"] = BosAv,
            ["Scouting:Adventurers:Rounds:0:Number"] = "1",
            ["Scouting:Adventurers:Rounds:0:Start"] = roundStart.ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:End"] = roundStart.Add(length ?? TimeSpan.FromMinutes(6)).ToGameTime().DateTime.ToString("s"),
            ["Scouting:Adventurers:Rounds:0:Themes:0"] = "1",
            ["Scouting:Adventurers:Rounds:0:Message"] = "Maak een foto van een bijzondere brievenbus."
        }, services => services.AddSingleton<TimeProvider>(_clock));
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
        PostsIn(ZeeAv).Single()["text"].Should().Contain("Ronde 1 is begonnen").And.Contain("thema 1").And.Contain("brievenbus");
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
    public async Task RoundLongPast_ShouldNotBePostedByItself()
    {
        await using var app = StartApp(_clock.GetUtcNow().AddHours(-3));
        await Task.Delay(500);

        _slack.Posts.Should().BeEmpty("a start of the app long after a round must not post old rounds");
    }
}
