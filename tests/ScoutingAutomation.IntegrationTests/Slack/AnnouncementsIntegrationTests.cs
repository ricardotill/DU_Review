using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// The game-start and game-end messages in every group channel, on a fake clock against a fake Slack API.
/// </summary>
public class AnnouncementsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] GroupChannels = { "C01ZEEARENDEN", "C02BOSUILEN", "C03WOLVEN", "C04POOLVOSSEN" };

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new();
    private readonly LogCapture _logs = new(LogLevel.Information);
    private readonly FakeTimeProvider _clock;

    public AnnouncementsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        // Whole minutes, like times set in the app
        var now = DateTimeOffset.UtcNow;
        _clock = new FakeTimeProvider(new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, TimeSpan.Zero));
    }

    private TestApp StartApp(Dictionary<string, string?> settings)
    {
        var app = TestApp.Live(_factory, _slack, settings, services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddSingleton<ILoggerProvider>(_logs);
        });
        _ = app.Factory.Services;
        return app;
    }

    /// <summary>A time setting in the game's time zone, as appsettings holds it.</summary>
    private string GameTimeSetting(TimeSpan fromNow) => _clock.GetUtcNow().Add(fromNow).ToGameTime().DateTime.ToString("s");

    private static T Service<T>(TestApp app) where T : notnull => app.Factory.Services.GetRequiredService<T>();

    [Fact]
    public async Task GameStart_ShouldBePostedOnceInEveryChannelWhenTheEventStarts()
    {
        var app = StartApp(new() { ["Scouting:EventStart"] = GameTimeSetting(TimeSpan.FromMinutes(10)) });
        try
        {
            await _logs.WaitForAsync("The game-start message is posted at");
            _slack.Posts.Should().BeEmpty("the event has not started yet");

            _clock.Advance(TimeSpan.FromMinutes(10));
            await _slack.WaitForAsync(s => s.Posts.Count == 4, "a game-start message in every channel");

            _slack.Posts.Select(p => p["channel"]).Should().BeEquivalentTo(GroupChannels);
            var text = _slack.Posts.Single(p => p["channel"] == "C01ZEEARENDEN")["text"];
            text.Should().Contain("Het spel is begonnen, De Zeearenden!")
                .And.Contain(GameStartAnnouncementConfig.RetryAllowedRule)
                .And.Contain(":thumbsup: :+1: :white_check_mark: goedgekeurd")
                .And.NotContain("{");
            Service<RuntimeSettingsService>(app).GameStartPostedAt("C01ZEEARENDEN").Should().Be(_clock.GetUtcNow());

            // The channels that got it are saved: after a restart nothing is posted again, unless asked
            app = await app.RestartAsync();
            var again = await Service<GameStartAnnouncer>(app).PostAsync(resend: false);
            again.Should().HaveCount(4).And.OnlyContain(r => r.Skipped);
            _slack.Posts.Should().HaveCount(4);

            var resent = await Service<GameStartAnnouncer>(app).PostAsync(resend: true);
            resent.Should().OnlyContain(r => r.Posted);
            _slack.Posts.Should().HaveCount(8);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task GameEnd_ShouldBePostedWithTheGroupAndEndTime()
    {
        await using var app = StartApp(new() { ["Scouting:EventEnd"] = GameTimeSetting(TimeSpan.FromMinutes(5)) });
        await _logs.WaitForAsync("The game-end message is posted at");

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _slack.WaitForAsync(s => s.Posts.Count == 4, "a game-end message in every channel");

        var end = _clock.GetUtcNow().ToGameTime().ToString("HH:mm");
        _slack.Posts.Single(p => p["channel"] == "C03WOLVEN")["text"]
            .Should().Contain($"Het spel is afgelopen, De Wolven!* Inzendingen vanaf {end} tellen niet meer mee");
        _slack.Posts.Should().OnlyContain(p => p["text"].Contains("afgelopen"), "there is no start time, so no start message");
    }

    [Fact]
    public async Task AppStartedLongAfterTheStart_ShouldNotPostByItselfButCanByHand()
    {
        await using var app = StartApp(new() { ["Scouting:EventStart"] = GameTimeSetting(TimeSpan.FromHours(-2)) });
        await _logs.WaitForAsync("passed more than");
        _slack.Posts.Should().BeEmpty("a message two hours late would only confuse");

        Service<RuntimeSettingsService>(app).SetText(SlackText.GameStart, "Welkom {group}, regel: {retryRule}").Should().BeNull();
        var results = await Service<GameStartAnnouncer>(app).PostAsync(resend: false);

        results.Should().HaveCount(4).And.OnlyContain(r => r.Posted);
        _slack.Posts.Single(p => p["channel"] == "C02BOSUILEN")["text"].Should().Be($"Welkom De Bosuilen, regel: {GameStartAnnouncementConfig.RetryAllowedRule}");
    }

    [Fact]
    public async Task FailedPost_ShouldNotBeMarkedSoItCanBeTriedAgain()
    {
        await using var app = StartApp(new() { ["Scouting:GameStartAnnouncement:AutoPost"] = "false" });
        var announcer = Service<GameStartAnnouncer>(app);
        _slack.Errors["chat.postMessage"] = "not_in_channel";

        var failed = await announcer.PostAsync(resend: false);

        failed.Should().HaveCount(4).And.OnlyContain(r => !r.Posted && r.Error == "not_in_channel");
        Service<RuntimeSettingsService>(app).GameStartPostedAt("C01ZEEARENDEN").Should().BeNull();

        _slack.Errors.Clear();
        (await announcer.PostAsync(resend: false)).Should().OnlyContain(r => r.Posted);
    }

    [Fact]
    public async Task AutoPostSwitchedOff_ShouldNotPostAtTheTime()
    {
        var at = GameTimeSetting(TimeSpan.FromMinutes(10));
        await using var app = StartApp(new()
        {
            ["Scouting:EventStart"] = at,
            ["Scouting:EventEnd"] = GameTimeSetting(TimeSpan.FromMinutes(20)),
            ["Scouting:GameStartAnnouncement:AutoPost"] = "false"
        });
        await _logs.WaitForAsync("The game-end message is posted at");

        _clock.Advance(TimeSpan.FromMinutes(20));
        await _slack.WaitForAsync(s => s.Posts.Count == 4, "the game-end message, which is still on");

        _slack.Posts.Should().OnlyContain(p => p["text"].Contains("afgelopen"));
    }
}
