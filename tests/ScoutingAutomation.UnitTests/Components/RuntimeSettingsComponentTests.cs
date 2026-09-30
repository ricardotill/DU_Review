using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Layout;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
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
        cut.Markup.Should().NotContain("Manual mode is on");

        cut.Find("#manualMode").Change(true);

        Runtime.ManualMode.Should().BeTrue();
        cut.Markup.Should().Contain("Manual mode is on");
        cut.Find("#manualMode").HasAttribute("checked").Should().BeTrue();

        cut.Find("#manualMode").Change(false);
        Runtime.ManualMode.Should().BeFalse();
    }

    [Fact]
    public void Dashboard_ShouldFollowSwitchesMadeElsewhere()
    {
        var cut = Render<Home>();

        Runtime.SetManualMode(true);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Manual mode is on"));
    }

    [Fact]
    public void DefaultFromAppSettings_ShouldApplyAtStart()
    {
        Settings.ManualMode = true;

        var cut = Render<Home>();

        cut.Markup.Should().Contain("Manual mode is on");
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

        var cut = Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "x"))));

        cut.Markup.Contains("Manual mode").Should().Be(manual);
    }

    [Fact]
    public void Configuration_ShouldShowDefaultAndCurrentMode()
    {
        Runtime.SetManualMode(true);

        var cut = Render<Configuration>();

        cut.FindAll("dt").Single(d => d.TextContent == "Manual mode").NextElementSibling!.TextContent
            .Should().Contain("On").And.Contain("default off");
    }
}
