using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Announcements;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class GameStartAnnouncerTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly List<(string Channel, string Text)> _posted = new();

    public GameStartAnnouncerTests()
    {
        _groups.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
        {
            new Group { Name = "De Wolven", SlackChannelId = "C1" },
            new Group { Name = "De Bosuilen", SlackChannelId = "C2" }
        });
        _slack.PostMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null)
            .AndDoes(ci => { lock (_posted) _posted.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))); });
    }

    public void Dispose() => _temp.Dispose();

    private ScoutingSettings Settings(DateTime? eventStart = null, bool autoPost = true, string? message = "Start {group}") => new()
    {
        EventStart = eventStart,
        RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
        GameStartAnnouncement = new GameStartAnnouncementConfig { AutoPost = autoPost, Message = message },
        Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" }
    };

    private (GameStartAnnouncer Announcer, RuntimeSettingsService Runtime) Create(ScoutingSettings settings, TimeProvider? time = null)
    {
        var options = Options.Create(settings);
        var runtime = TestRuntime.For(options);
        var services = new ServiceCollection()
            .AddSingleton(_groups)
            .AddSingleton(_slack)
            .BuildServiceProvider();
        var announcer = new GameStartAnnouncer(services.GetRequiredService<IServiceScopeFactory>(), options, runtime,
            time ?? TimeProvider.System, NullLogger<GameStartAnnouncer>.Instance);
        return (announcer, runtime);
    }

    [Fact]
    public void Render_ShouldFillTheGroupAndEveryEmojiPlaceholder()
    {
        var settings = new ScoutingSettings
        {
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup", ":+1:", " ", "THUMBSUP" },
                Awesome = new List<string> { "fire" },
                Rejected = new List<string>()
            }
        };
        settings.SlackFeedback.DownloadFailed.Reaction = null;

        var text = GameStartAnnouncer.Render(
            "{group}|{approved}|{awesome}|{rejected}|{missingNumber}|{unknownNumber}|{unsupportedFile}|{downloadFailed}|{other}",
            "De Wolven", settings);

        text.Should().Be("De Wolven|:thumbsup: :+1:|:fire:||:question:|:question:|:warning:||{other}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Render_RetryRule_ShouldFollowTheBlockSetting(bool block)
    {
        var settings = new ScoutingSettings
        {
            ReviewStatusReactions = new ReviewStatusReactionsConfig { Approved = new List<string> { "thumbsup" }, Awesome = new List<string> { "fire" } }
        };

        var text = GameStartAnnouncer.Render("{retryRule}", "De Wolven", settings, block);

        if (block)
        {
            text.Should().Contain("tot je opdracht is goedgekeurd").And.Contain(":thumbsup:").And.Contain(":fire:").And.Contain(":lock:");
        }
        else
        {
            text.Should().Be(GameStartAnnouncementConfig.RetryAllowedRule);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DefaultMessage_ShouldHaveNoPlaceholdersLeftAfterRendering(bool block)
    {
        var settings = new ScoutingSettings
        {
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" }, Awesome = new List<string> { "star" }, Rejected = new List<string> { "x" }
            }
        };

        var text = GameStartAnnouncer.Render(GameStartAnnouncementConfig.DefaultMessage, "De Wolven", settings, block);

        text.Should().Contain("De Wolven").And.Contain(":thumbsup:").And.Contain(":star:").And.Contain(":x:").And.NotContain("{");
    }

    [Fact]
    public async Task PostAsync_ShouldPostOncePerChannelAndSkipChannelsThatGotIt()
    {
        var (announcer, runtime) = Create(Settings());

        var first = await announcer.PostAsync(resend: false);
        var second = await announcer.PostAsync(resend: false);

        _posted.Should().BeEquivalentTo(new[] { ("C1", "Start De Wolven"), ("C2", "Start De Bosuilen") });
        first.Should().OnlyContain(r => r.Posted);
        second.Should().OnlyContain(r => r.Skipped);
        runtime.GameStartPostedAt("C1").Should().NotBeNull();
    }

    [Fact]
    public async Task PostAsync_ShouldRememberPostedChannelsAfterARestart()
    {
        var settings = Settings();
        await Create(settings).Announcer.PostAsync(resend: false);

        var (restarted, _) = Create(settings);
        await restarted.PostAsync(resend: false);

        _posted.Should().HaveCount(2);
    }

    [Fact]
    public async Task PostAsync_WithResend_ShouldPostInEveryChannelAgain()
    {
        var (announcer, _) = Create(Settings());

        await announcer.PostAsync(resend: false);
        var again = await announcer.PostAsync(resend: true);

        _posted.Should().HaveCount(4);
        again.Should().OnlyContain(r => r.Posted);
    }

    [Theory]
    [InlineData("channel_not_found")]
    [InlineData("simulation_mode")]
    public async Task PostAsync_ShouldNotMarkAChannelWhenPostingFails(string error)
    {
        _slack.PostMessageAsync("C1", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(error);
        var (announcer, runtime) = Create(Settings());

        var results = await announcer.PostAsync(resend: false);

        results.Single(r => r.ChannelId == "C1").Error.Should().Be(error);
        runtime.GameStartPostedAt("C1").Should().BeNull();
        runtime.GameStartPostedAt("C2").Should().NotBeNull();
    }

    [Fact]
    public async Task PostAsync_ShouldUseTheTextChangedInTheApp()
    {
        var (announcer, runtime) = Create(Settings());
        runtime.SetText(SlackText.GameStart, "Go {group}!");

        await announcer.PostAsync(resend: false);

        _posted.Select(p => p.Text).Should().Contain("Go De Wolven!");
    }

    [Fact]
    public async Task PostAsync_CalledTwiceAtOnce_ShouldPostOnce()
    {
        var (announcer, _) = Create(Settings());

        await Task.WhenAll(announcer.PostAsync(resend: false), announcer.PostAsync(resend: false));

        _posted.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task PostAsync_WithoutAMessage_ShouldPostNothing(string? message)
    {
        var (announcer, _) = Create(Settings(message: message));

        (await announcer.PostAsync(resend: false)).Should().BeEmpty();
        _posted.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoPost_ShouldPostWhenTheEventStarts()
    {
        var start = new DateTime(2026, 10, 3, 9, 0, 0);
        var time = new FixedTimeProvider(GameTime.FromGameTime(start) - TimeSpan.FromMilliseconds(50));
        var (announcer, _) = Create(Settings(eventStart: start), time);

        await RunAsync(announcer, () => Posted >= 2);

        _posted.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(61, false)]
    public async Task AutoPost_AfterTheEventStarted_ShouldOnlyPostWithinTheWindow(int minutesLate, bool posts)
    {
        var start = new DateTime(2026, 10, 3, 9, 0, 0);
        var time = new FixedTimeProvider(GameTime.FromGameTime(start).AddMinutes(minutesLate));
        var (announcer, _) = Create(Settings(eventStart: start), time);

        await RunAsync(announcer, () => Posted >= 2, posts ? null : NothingWithin);

        _posted.Should().HaveCount(posts ? 2 : 0);
    }

    [Fact]
    public async Task AutoPost_ShouldNotPostWithoutAnEventStartOrWhenSwitchedOff()
    {
        var (withoutStart, _) = Create(Settings(eventStart: null));
        var (switchedOff, _) = Create(Settings(eventStart: DateTimeOffset.UtcNow.ToGameTime().DateTime, autoPost: false));

        await RunAsync(withoutStart, () => false, NothingWithin);
        await RunAsync(switchedOff, () => false, NothingWithin);

        _posted.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoPost_ShouldFollowAStartTimeChangedInTheApp()
    {
        var now = new DateTime(2026, 10, 3, 9, 0, 0);
        var time = new FixedTimeProvider(GameTime.FromGameTime(now));
        // Far away: nothing would be posted while the test runs
        var (announcer, runtime) = Create(Settings(eventStart: now.AddHours(5)), time);

        await announcer.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            _posted.Should().BeEmpty();

            runtime.SetEventStart(now).Should().BeNull();
            await WaitForAsync(() => Posted >= 2);
        }
        finally
        {
            await announcer.StopAsync(CancellationToken.None);
        }

        _posted.Should().HaveCount(2);
    }

    private int Posted
    {
        get { lock (_posted) return _posted.Count; }
    }

    /// <summary>Runs the announcer until <paramref name="done"/> holds or a short time passed, then stops it.</summary>
    /// <summary>How long a test watches for a post that should not come.</summary>
    internal static readonly TimeSpan NothingWithin = TimeSpan.FromMilliseconds(500);

    /// <param name="timeout">
    /// How long to wait for <paramref name="done"/>: 5 seconds by default, because the first start of the service (scope,
    /// database) can take seconds on a busy CI machine; <see cref="NothingWithin"/> when nothing should happen.
    /// </param>
    internal static async Task RunAsync(ScheduledAnnouncer announcer, Func<bool> done, TimeSpan? timeout = null)
    {
        await announcer.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(done, timeout ?? TimeSpan.FromSeconds(5));
        }
        finally
        {
            await announcer.StopAsync(CancellationToken.None);
        }
    }

    internal static async Task WaitForAsync(Func<bool> done, TimeSpan? timeout = null)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!done() && DateTime.UtcNow < until)
        {
            await Task.Delay(20);
        }
    }

    /// <summary>A fixed "now"; timers still run on the real clock.</summary>
    internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
