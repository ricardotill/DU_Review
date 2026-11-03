using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>
/// The start and end of the Avonturiers part, which are also when the start information and final location go out:
/// from appsettings, changed in the app, otherwise the event's.
/// </summary>
public class AdventurersPostTimeSettingsTests : IDisposable
{
    private static readonly DateTime StartAt = new(2026, 10, 5, 12, 0, 0);
    private static readonly DateTime FinalAt = new(2026, 10, 5, 17, 30, 0);
    private static readonly DateTime EventStart = new(2026, 10, 5, 11, 0, 0);
    private static readonly DateTime EventEnd = new(2026, 10, 5, 18, 0, 0);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RuntimeSettingsService Create(DateTime? eventStart = null, DateTime? eventEnd = null, bool own = true) =>
        new(Options.Create(new ScoutingSettings
            {
                RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
                EventStart = eventStart,
                EventEnd = eventEnd,
                Adventurers = new AdventurersSettings
                {
                    Enabled = true,
                    StartAnnouncement = new AdventurersAnnouncementConfig { PostAt = own ? StartAt : null },
                    FinalAnnouncement = new AdventurersAnnouncementConfig { PostAt = own ? FinalAt : null }
                }
            }),
            NullLogger<RuntimeSettingsService>.Instance);

    [Fact]
    public void FromAppsettings_ShouldBeUsed()
    {
        var runtime = Create();

        runtime.AdventurersStartAtGameTime.Should().Be(StartAt);
        runtime.AdventurersFinalAtGameTime.Should().Be(FinalAt);
        runtime.IsAdventurersPostTimeChanged(final: false).Should().BeFalse();
    }

    [Fact]
    public void SetInTheApp_ShouldWinAndSurviveARestart()
    {
        Create().SetAdventurersPostTime(final: true, StartAt.AddHours(4)).Should().BeNull();

        var restarted = Create();
        restarted.AdventurersFinalAtGameTime.Should().Be(StartAt.AddHours(4));
        restarted.IsAdventurersPostTimeChanged(final: true).Should().BeTrue();
        restarted.AdventurersStartAtGameTime.Should().Be(StartAt, "only the final time was changed");
    }

    [Fact]
    public void Reset_ShouldGoBackToAppsettings()
    {
        var runtime = Create();
        runtime.SetAdventurersPostTime(final: false, StartAt.AddHours(1));

        runtime.ResetAdventurersPostTime(final: false);

        runtime.AdventurersStartAtGameTime.Should().Be(StartAt);
        runtime.IsAdventurersPostTimeChanged(final: false).Should().BeFalse();
        Create().AdventurersStartAtGameTime.Should().Be(StartAt, "the reset is saved");
    }

    [Fact]
    public void WithoutOwnTimes_ShouldFollowTheEvent()
    {
        var runtime = Create(eventStart: EventStart, eventEnd: EventEnd, own: false);

        runtime.AdventurersStartAtGameTime.Should().Be(EventStart);
        runtime.AdventurersFinalAtGameTime.Should().Be(EventEnd);
        runtime.AdventurersOwnTime(final: false).Should().BeNull();
    }

    [Fact]
    public void EndBeforeStart_ShouldBeRefused()
    {
        var runtime = Create();

        runtime.SetAdventurersPostTime(final: true, StartAt.AddMinutes(-1)).Should().Contain("na de start");
        runtime.SetAdventurersPostTime(final: false, FinalAt).Should().Contain("na de start");
        runtime.AdventurersFinalAtGameTime.Should().Be(FinalAt);
    }

    [Fact]
    public void Window_ShouldBeOwnPerPart()
    {
        // The event runs 11:00-18:00, the Avonturiers 12:00-17:30
        var runtime = Create(eventStart: EventStart, eventEnd: EventEnd);
        var elevenThirty = Ts(EventStart.AddMinutes(30));
        var quarterToSix = Ts(FinalAt.AddMinutes(15));

        runtime.IsBeforeStart(elevenThirty, adventurers: false).Should().BeFalse();
        runtime.IsBeforeStart(elevenThirty, adventurers: true).Should().BeTrue();
        runtime.IsAfterEnd(quarterToSix, adventurers: false).Should().BeFalse();
        runtime.IsAfterEnd(quarterToSix, adventurers: true).Should().BeTrue();
    }

    [Fact]
    public void History_ShouldBeReadFromTheEarliestStart()
    {
        var since = GameTime.FromGameTime(new DateTime(2026, 10, 5, 0, 0, 0));

        // The Avonturiers start at 12:00, before an event that starts at 13:00
        Create(eventStart: StartAt.AddHours(1)).NotBeforeEventStart(since).Should().Be(GameTime.FromGameTime(StartAt));
        Create(eventStart: EventStart).NotBeforeEventStart(since).Should().Be(GameTime.FromGameTime(EventStart));
        Create().NotBeforeEventStart(since).Should().Be(since, "the Toeristen have no start");
    }

    private static string Ts(DateTime gameTime) => $"{GameTime.FromGameTime(gameTime).ToUnixTimeSeconds()}.000000";
}
