using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Layout;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class ManualModeComponentTests : PageTestBase
{
    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    [Fact]
    public void DashboardSwitch_ShouldTurnManualModeOnAndOff()
    {
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".spinner-border").Should().BeEmpty());
        cut.Find("#manualMode").HasAttribute("checked").Should().BeFalse();
        cut.Markup.Should().NotContain("Handmatige modus staat aan");

        cut.Find("#manualMode").Change(true);

        Runtime.ManualMode.Should().BeTrue();
        cut.Markup.Should().Contain("Handmatige modus staat aan");
        cut.Find("#manualMode").HasAttribute("checked").Should().BeTrue();

        cut.Find("#manualMode").Change(false);
        Runtime.ManualMode.Should().BeFalse();
    }

    [Fact]
    public void Dashboard_ShouldFollowSwitchesMadeElsewhere()
    {
        var cut = Render<Home>();

        Runtime.SetManualMode(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Handmatige modus staat aan"));
    }

    [Fact]
    public void DefaultFromAppSettings_ShouldApplyAtStart()
    {
        Settings.ManualMode = true;

        var cut = Render<Home>();

        cut.Markup.Should().Contain("Handmatige modus staat aan");
    }

    [Fact]
    public async Task ClosedDashboard_ShouldStopListening()
    {
        Render<Home>();
        await DisposeComponentsAsync();

        var act = () => Runtime.SetManualMode(true);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TopBar_ShouldShowManualModeBadge(bool manual)
    {
        Runtime.SetManualMode(manual);

        var cut = RenderLayout("x");

        cut.Markup.Contains("Handmatige modus").Should().Be(manual);
    }

    [Fact]
    public void Configuration_ShouldShowDefaultAndCurrentMode()
    {
        Runtime.SetManualMode(true);

        var cut = Render<Configuration>();

        cut.FindAll("dt").Single(d => d.TextContent == "Handmatige modus").NextElementSibling!.TextContent
            .Should().Contain("Aan").And.Contain("standaard uit");
    }
}
