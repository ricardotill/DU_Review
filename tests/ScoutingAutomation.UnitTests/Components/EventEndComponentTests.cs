using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>The end of the game in the app: the badge in the top bar, the countdown, and the panels on the configuration page.</summary>
public class EventEndComponentTests : PageTestBase
{
    private static DateTime GameNow => DateTimeOffset.UtcNow.ToGameTime().DateTime;

    [Fact]
    public void Layout_ShouldShowTheEndOnlyAfterTheEvent()
    {
        Settings.EventEnd = GameNow.AddHours(1);
        RenderLayout("page body").Markup.Should().NotContain("Spel afgelopen");

        Settings.EventEnd = GameNow.AddHours(-1);
        var cut = RenderLayout("page body");

        cut.Find(".game-ended").TextContent.Should().Contain("Spel afgelopen om " + Settings.EventEnd.Value.ToString("HH:mm"));
    }

    [Fact]
    public void Layout_ShouldShowWaitingBeforeTheStartAndBusyDuringTheGame()
    {
        RenderLayout("page body").FindAll(".game-status").Should().BeEmpty();

        Settings.EventStart = GameNow.AddHours(1);
        RenderLayout("page body").Find(".game-waiting").TextContent
            .Should().Contain("Wacht op start · " + Settings.EventStart.Value.ToString("HH:mm"));

        Settings.EventStart = GameNow.AddHours(-1);
        Settings.EventEnd = GameNow.AddHours(1);
        RenderLayout("page body").Find(".game-busy").TextContent
            .Should().Contain("Spel bezig · tot " + Settings.EventEnd.Value.ToString("HH:mm"));
    }

    [Fact]
    public void Layout_ShouldShowBusyWithoutAnEnd()
    {
        Settings.EventStart = GameNow.AddHours(-1);

        var tag = RenderLayout("page body").Find(".game-busy");

        tag.TextContent.Trim().Should().Be("Spel bezig");
    }

    [Fact]
    public void Layout_ShouldShowTheEndWhenItIsSetInTheApp()
    {
        var cut = RenderLayout("page body");
        cut.FindAll(".game-ended").Should().BeEmpty();

        Services.GetRequiredService<RuntimeSettingsService>().SetEventEnd(GameNow.AddMinutes(-5));

        cut.WaitForAssertion(() => cut.FindAll(".game-ended").Should().ContainSingle());
    }

    [Fact]
    public void Countdown_ShouldShowTheTimeLeftOrThatTheGameEnded()
    {
        Render<GameCountdown>().Markup.Trim().Should().BeEmpty();

        Settings.EventEnd = GameNow.AddHours(2);
        Render<GameCountdown>().Find(".game-countdown").TextContent.Should().MatchRegex(@"Nog (1:59:\d\d|2:00:00)");

        Settings.EventEnd = GameNow.AddHours(-1);
        var ended = Render<GameCountdown>().Find(".game-countdown");
        ended.TextContent.Should().Contain("Spel afgelopen");
        ended.ClassList.Should().Contain("ended");
    }

    [Theory]
    [InlineData(0.4, "0:00:01")]
    [InlineData(61, "0:01:01")]
    [InlineData(3 * 3600 + 5 * 60 + 9, "3:05:09")]
    public void Countdown_Format_ShouldRoundUp(double seconds, string expected) =>
        GameCountdown.Format(TimeSpan.FromSeconds(seconds)).Should().Be(expected);

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public void EventTimesPanel_ShouldKeepATimeWithSecondsAsTheBrowserSendsIt(string field)
    {
        var cut = Render<EventTimesPanel>();

        // Blazor sends a datetime-local value with seconds; it used to fail to parse and empty the field
        cut.Find($".{field}-input").Change("2026-10-03T10:30:00");
        cut.Find($"form:has(.{field}-input)").Submit();

        var runtime = Services.GetRequiredService<RuntimeSettingsService>();
        (field == "start" ? runtime.EventStartGameTime : runtime.EventEndGameTime).Should().Be(new DateTime(2026, 10, 3, 10, 30, 0));
        cut.Find($".{field}-input").GetAttribute("value").Should().StartWith("2026-10-03T10:30");
    }

    [Fact]
    public void EventTimesPanel_ShouldSaveAValidEndAndRefuseOneBeforeTheStart()
    {
        Settings.EventStart = new DateTime(2026, 10, 3, 9, 0, 0);
        var cut = Render<EventTimesPanel>();
        var runtime = Services.GetRequiredService<RuntimeSettingsService>();

        cut.Find(".end-input").Change("2026-10-03T08:00:00");
        cut.Find("form:has(.end-input)").Submit();
        cut.Markup.Should().Contain("De eindtijd moet na de starttijd liggen.");
        runtime.EventEndGameTime.Should().BeNull();

        cut.Find(".end-input").Change("2026-10-03T17:00:00");
        cut.Find("form:has(.end-input)").Submit();

        runtime.EventEndGameTime.Should().Be(new DateTime(2026, 10, 3, 17, 0, 0));
        cut.WaitForAssertion(() => cut.Find(".event-end").TextContent.Should().Be("03-10-2026 17:00"));
        cut.Markup.Should().Contain("Aangepast in de app");
    }

    [Fact]
    public void Configuration_ShouldShowTheEndPanelsAndTheTooLateFeedback()
    {
        Seed();

        var cut = Render<Configuration>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Eindbericht in Slack"));
        cut.Markup.Should().Contain("Startbericht in Slack").And.Contain("Speeltijd").And.Contain("Na de eindtijd").And.Contain(":alarm_clock:");
    }
}
