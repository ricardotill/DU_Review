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
using static ScoutingAutomation.UnitTests.Slack.GameStartAnnouncerTests;

namespace ScoutingAutomation.UnitTests.Slack;

public class GameEndAnnouncerTests : IDisposable
{
    private static readonly DateTime End = new(2026, 10, 3, 17, 0, 0);

    private readonly TempDirectory _temp = new();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly List<(string Channel, string Text)> _posted = new();

    public GameEndAnnouncerTests()
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

    private int Posted
    {
        get { lock (_posted) return _posted.Count; }
    }

    private ScoutingSettings Settings(DateTime? eventEnd = null, bool autoPost = true, string? message = "Einde {group} om {end}") => new()
    {
        EventEnd = eventEnd,
        RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
        GameEndAnnouncement = new GameEndAnnouncementConfig { AutoPost = autoPost, Message = message },
        Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" }
    };

    private (GameEndAnnouncer Announcer, RuntimeSettingsService Runtime) Create(ScoutingSettings settings, TimeProvider? time = null)
    {
        var options = Options.Create(settings);
        var runtime = TestRuntime.For(options);
        var services = new ServiceCollection()
            .AddSingleton(_groups)
            .AddSingleton(_slack)
            .BuildServiceProvider();
        var announcer = new GameEndAnnouncer(services.GetRequiredService<IServiceScopeFactory>(), options, runtime,
            time ?? TimeProvider.System, NullLogger<GameEndAnnouncer>.Instance);
        return (announcer, runtime);
    }

    [Fact]
    public void Render_ShouldFillTheGroupAndTheEndTime()
    {
        GameEndAnnouncer.Render("Einde {group} om {end}", "De Wolven", new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero))
            .Should().Be("Einde De Wolven om 17:00");
        GameEndAnnouncer.Render("om {end}", "De Wolven", null).Should().Be("om ");
    }

    [Fact]
    public async Task PostAsync_ShouldPostOncePerChannelAndSurviveARestart()
    {
        var (announcer, runtime) = Create(Settings(End));

        await announcer.PostAsync(resend: false);
        await Create(Settings(End)).Announcer.PostAsync(resend: false);

        _posted.Should().Equal(("C1", "Einde De Wolven om 17:00"), ("C2", "Einde De Bosuilen om 17:00"));
        runtime.GameEndPostedAt("C1").Should().NotBeNull();
        runtime.GameStartPostedAt("C1").Should().BeNull();
    }

    [Fact]
    public async Task PostAsync_Resend_ShouldPostAgain()
    {
        var (announcer, _) = Create(Settings(End));

        await announcer.PostAsync(resend: false);
        await announcer.PostAsync(resend: true);

        _posted.Should().HaveCount(4);
    }

    [Fact]
    public async Task AutoPost_ShouldPostWhenTheEventEnds()
    {
        var time = new FixedTimeProvider(GameTime.FromGameTime(End) - TimeSpan.FromMilliseconds(50));
        var (announcer, _) = Create(Settings(End), time);

        await RunAsync(announcer, () => Posted >= 2);

        _posted.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(30, true)]
    [InlineData(61, false)]
    public async Task AutoPost_AfterTheEventEnded_ShouldOnlyPostWithinTheWindow(int minutesLate, bool posts)
    {
        var time = new FixedTimeProvider(GameTime.FromGameTime(End).AddMinutes(minutesLate));
        var (announcer, _) = Create(Settings(End), time);

        await RunAsync(announcer, () => Posted >= 2, posts ? null : NothingWithin);

        _posted.Should().HaveCount(posts ? 2 : 0);
    }

    [Fact]
    public async Task AutoPost_ShouldNotPostWithoutAnEventEndOrWhenSwitchedOff()
    {
        var (withoutEnd, _) = Create(Settings(eventEnd: null));
        var (switchedOff, _) = Create(Settings(eventEnd: DateTimeOffset.UtcNow.ToGameTime().DateTime, autoPost: false));

        await RunAsync(withoutEnd, () => false, NothingWithin);
        await RunAsync(switchedOff, () => false, NothingWithin);

        _posted.Should().BeEmpty();
    }

    [Fact]
    public async Task AutoPost_ShouldFollowAnEndTimeChangedInTheApp()
    {
        var time = new FixedTimeProvider(GameTime.FromGameTime(End));
        var (announcer, runtime) = Create(Settings(eventEnd: null), time);

        await announcer.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(100);
            _posted.Should().BeEmpty();

            runtime.SetEventEnd(End).Should().BeNull();
            await WaitForAsync(() => Posted >= 2);
            // Other changes afterwards do not post again
            runtime.SetText(SlackText.GameEnd, "Nog een keer");
            await Task.Delay(100);
        }
        finally
        {
            await announcer.StopAsync(CancellationToken.None);
        }

        _posted.Should().HaveCount(2);
    }
}
