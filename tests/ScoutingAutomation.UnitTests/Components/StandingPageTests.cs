using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>The jury hides the standing, plans periods and publishes an interim standing; the beamer follows when asked.</summary>
public class StandingPageTests : PageTestBase
{
    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    private IRenderedComponent<Standing> RenderPage()
    {
        Seed();
        LogInAsJury();
        var cut = Render<Standing>();
        cut.WaitForAssertion(() => cut.Find(".publish-interim").HasAttribute("disabled").Should().BeFalse());
        return cut;
    }

    [Fact]
    public void Switch_ShouldHideTheStandingForGroups()
    {
        var cut = RenderPage();
        cut.Find(".standing-state").TextContent.Should().Contain("Zichtbaar");

        cut.Find("#hideNow").Change(true);

        Runtime.StandingHiddenManually.Should().BeTrue();
        cut.WaitForAssertion(() => cut.Find(".standing-state").TextContent.Should().Contain("Verborgen"));

        cut.Find("#hideBeamer").Change(true);
        Runtime.HideStandingOnBeamer.Should().BeTrue();
    }

    [Fact]
    public void Period_ShouldBeAddedAndRemoved()
    {
        var cut = RenderPage();

        cut.Find(".window-from").Change("2026-10-03T15:00:00");
        cut.Find(".window-to").Change("2026-10-03T17:00:00");
        cut.Find("form").Submit();

        Runtime.StandingHideWindows.Should().ContainSingle(w => w.From == new DateTime(2026, 10, 3, 15, 0, 0) && w.To == new DateTime(2026, 10, 3, 17, 0, 0));
        cut.WaitForAssertion(() => cut.Find(".hide-windows").TextContent.Should().Contain("03-10-2026 15:00").And.Contain("03-10-2026 17:00"));

        cut.Find(".remove-window").Click();

        Runtime.StandingHideWindows.Should().BeEmpty();
    }

    [Fact]
    public void Period_WithTheEndBeforeTheStart_ShouldBeRefused()
    {
        var cut = RenderPage();

        cut.Find(".window-from").Change("2026-10-03T17:00:00");
        cut.Find(".window-to").Change("2026-10-03T15:00:00");
        cut.Find("form").Submit();

        Runtime.StandingHideWindows.Should().BeEmpty();
        cut.Markup.Should().Contain("Het einde moet na het begin liggen.");
    }

    [Fact]
    public void Publish_ShouldFreezeTheCurrentStanding()
    {
        var cut = RenderPage();

        cut.Find(".publish-interim").Click();

        cut.WaitForAssertion(() => Runtime.InterimStanding.Should().NotBeNull());
        Runtime.InterimStanding!.Rows.Select(r => (r.GroupName, r.Rank, r.Score)).Should().Equal(
            ("De Wolven", 1, 5), ("De Bosuilen", 2, 0), ("De Vossen", 2, 0));
        cut.WaitForAssertion(() => cut.FindAll(".interim-table tbody tr").Should().HaveCount(3));

        cut.Find(".clear-interim").Click();

        Runtime.InterimStanding.Should().BeNull();
    }

    [Fact]
    public void Group_ShouldNotBeAbleToChangeAnything()
    {
        var cut = RenderPage();
        // A tab whose login became a group's (cannot happen through the menu, but a tab could still send the click)
        LogInAsGroup(Wolves);

        cut.Find("#hideNow").Change(true);
        cut.Find(".publish-interim").Click();

        Runtime.StandingHiddenManually.Should().BeFalse();
        Runtime.InterimStanding.Should().BeNull();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Alleen voor de jury en beheerders."));
    }

    [Fact]
    public void Scoreboard_ShouldOnlyHideWhenAskedForTheBeamer()
    {
        Seed();
        Runtime.SetStandingHidden(true);

        var cut = Render<Scoreboard>();
        cut.WaitForAssertion(() => cut.FindAll(".board-row").Should().HaveCount(3));

        Runtime.SetHideStandingOnBeamer(true);
        cut.WaitForAssertion(() => cut.FindAll(".board-row").Should().BeEmpty());
        cut.Find(".beamer-hidden").TextContent.Should().Contain("tijdelijk verborgen");
        cut.FindAll(".beamer-chart").Should().BeEmpty();

        Runtime.PublishInterimStanding([new InterimStandingRow(Owls.Id, "De Bosuilen", 1, 7)], new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        cut.WaitForAssertion(() => cut.FindAll(".board-row").Should().ContainSingle().Which.TextContent.Should().Contain("De Bosuilen").And.Contain("7"));
        cut.Find(".beamer-interim").TextContent.Should().Contain("Tussenstand van 14:00");
    }
}
