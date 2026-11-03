using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>The start and end of the event, set in appsettings and changeable in the app.</summary>
public class EventTimeSettingsTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 3, 9, 0, 0);
    private static readonly DateTime End = new(2026, 10, 3, 17, 0, 0);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RuntimeSettingsService Create(DateTime? start = null, DateTime? end = null) =>
        TestRuntime.For(Options.Create(new ScoutingSettings
        {
            EventStart = start,
            EventEnd = end,
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json")
        }));

    [Fact]
    public void Times_ShouldComeFromAppSettingsUntilChanged()
    {
        var runtime = Create(Start, End);

        runtime.EventStartGameTime.Should().Be(Start);
        runtime.EventEndGameTime.Should().Be(End);
        runtime.IsEventStartChanged.Should().BeFalse();
        runtime.IsEventEndChanged.Should().BeFalse();
    }

    [Fact]
    public void SetTimes_ShouldBeUsedAndSurviveARestart()
    {
        var runtime = Create(Start, End);
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.SetEventStart(Start.AddHours(1)).Should().BeNull();
        runtime.SetEventEnd(End.AddHours(1)).Should().BeNull();

        changes.Should().Be(2);
        var restarted = Create(Start, End);
        restarted.EventStartGameTime.Should().Be(Start.AddHours(1));
        restarted.EventEndGameTime.Should().Be(End.AddHours(1));
        restarted.IsEventStartChanged.Should().BeTrue();
        restarted.EventEnd.Should().Be(new DateTimeOffset(2026, 10, 3, 16, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SetTime_Null_ShouldRemoveTheTimeFromAppSettings()
    {
        var runtime = Create(Start, End);

        runtime.SetEventEnd(null).Should().BeNull();

        runtime.EventEnd.Should().BeNull();
        runtime.IsEventEndChanged.Should().BeTrue();
        Create(Start, End).EventEnd.Should().BeNull();
    }

    [Fact]
    public void SetTime_ShouldDropSeconds()
    {
        var runtime = Create();

        runtime.SetEventEnd(End.AddSeconds(42));

        runtime.EventEndGameTime.Should().Be(End);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SetTime_EndNotAfterStart_ShouldBeRefused(int hours)
    {
        var runtime = Create(Start, End);

        runtime.SetEventEnd(Start.AddHours(hours)).Should().NotBeNull();
        runtime.SetEventStart(End.AddHours(-hours)).Should().NotBeNull();

        runtime.EventStartGameTime.Should().Be(Start);
        runtime.EventEndGameTime.Should().Be(End);
        runtime.IsEventEndChanged.Should().BeFalse();
    }

    [Fact]
    public void ResetTimes_ShouldGoBackToAppSettings()
    {
        var runtime = Create(Start, End);
        runtime.SetEventStart(null);
        runtime.SetEventEnd(null);

        runtime.ResetEventStart();
        runtime.ResetEventEnd();

        runtime.EventStartGameTime.Should().Be(Start);
        runtime.EventEndGameTime.Should().Be(End);
        Create(Start, End).IsEventEndChanged.Should().BeFalse();
    }

    [Fact]
    public void GameEndPosted_ShouldSurviveARestart()
    {
        var at = new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
        Create().MarkGameEndPosted("C1", at);

        var restarted = Create();
        restarted.GameEndPostedAt("C1").Should().Be(at);
        restarted.GameStartPostedAt("C1").Should().BeNull();
    }

    [Fact]
    public void GameEndAndTooLateTexts_ShouldBeChangeable()
    {
        var runtime = Create();

        runtime.Text(SlackText.GameEnd).Should().Be(GameEndAnnouncementConfig.DefaultMessage);
        runtime.Feedback.TooLate.Reaction.Should().Be("alarm_clock");

        runtime.SetText(SlackText.TooLate, "Te laat!");

        runtime.Feedback.TooLate.Message.Should().Be("Te laat!");
        runtime.Feedback.TooLate.Reaction.Should().Be("alarm_clock");
    }
}
