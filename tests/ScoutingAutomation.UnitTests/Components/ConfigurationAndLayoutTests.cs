using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Layout;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class ConfigurationPageTests : PageTestBase
{
    private string Row(IRenderedComponent<Configuration> cut, string label) =>
        cut.FindAll("dt").Single(dt => dt.TextContent == label).NextElementSibling!.TextContent;

    [Fact]
    public void Simulation_ShouldShowGroupsReviewersFeedbackAndFiles()
    {
        Seed();
        Settings.SlackFeedback.UnsupportedFile = new FeedbackConfig();

        var cut = Render<Configuration>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(3));
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("De Vossen")).TextContent.Should().Contain("not set").And.Contain("–");
        cut.Find("a[href^='groups/']").GetAttribute("href").Should().StartWith("groups/");
        cut.Markup.Should().Contain("U_JURY").And.Contain("Only reactions from these Slack users count");
        cut.Markup.Should().Contain(":thumbsup:").And.Contain("/approved/").And.Contain("/awesome/").And.Contain("/rejected/");
        cut.Markup.Should().Contain("no reaction").And.Contain("No reply").And.Contain(":question:");
        cut.Markup.Should().Contain(".jpg").And.Contain("image/*");
        Row(cut, "Assignments").Should().Contain("3 loaded");
        Row(cut, "Slack mode").Should().Contain("Simulation");
        Row(cut, "Signing secret").Should().Contain("not checked in simulation");
        Row(cut, "Bot token").Should().Contain("not set");
        cut.FindAll("dt").Select(d => d.TextContent).Should().NotContain("Connection");
    }

    [Fact]
    public void NoReviewers_ShouldWarnThatReactionsAreIgnored()
    {
        Settings.Reviewers = new List<string>();

        var cut = Render<Configuration>();

        cut.Markup.Should().Contain("No reviewers configured");
    }

    [Theory]
    [InlineData("", "not set: all Slack requests are rejected")]
    [InlineData("secret", "set, requests are verified")]
    public void EventsApi_ShouldShowSigningSecretState(string secret, string expected)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SigningSecret = secret, BotToken = "xoxb" };

        var cut = Render<Configuration>();

        Row(cut, "Slack mode").Should().Contain("Live");
        Row(cut, "Connection").Should().Contain("Events API");
        Row(cut, "Signing secret").Should().Contain(expected);
        Row(cut, "Bot token").Should().Contain("set");
        cut.FindAll("dt").Select(d => d.TextContent).Should().NotContain("App token");
    }

    [Theory]
    [InlineData("", SlackConnectionState.Error, "App token not set", "badge-rejected", "not set: no events are received")]
    [InlineData("xapp", SlackConnectionState.Connected, null, "badge-approved", "set")]
    [InlineData("xapp", SlackConnectionState.Connecting, null, "badge-pending", "set")]
    [InlineData("xapp", SlackConnectionState.Disabled, null, "badge-received", "set")]
    public void SocketMode_ShouldShowConnectionState(string appToken, SlackConnectionState state, string? detail, string badge, string tokenText)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = appToken };
        Services.GetRequiredService<SlackConnectionStatus>().Set(state, detail);

        var cut = Render<Configuration>();

        var connection = cut.FindAll("dt").Single(dt => dt.TextContent == "Connection").NextElementSibling!;
        connection.TextContent.Should().Contain("Socket Mode").And.Contain(state.ToString());
        connection.QuerySelector(".badge")!.ClassList.Should().Contain(badge);
        if (detail != null) connection.TextContent.Should().Contain(detail);
        Row(cut, "App token").Should().Contain(tokenText);
        Row(cut, "Signing secret").Should().Contain("not needed in Socket Mode");
    }
}

public class LayoutTests : PageTestBase
{
    private IRenderedComponent<MainLayout> RenderLayout() =>
        Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "page body"))));

    [Fact]
    public void Simulation_ShouldShowSimulationBadgeAndSimulatorLink()
    {
        var cut = RenderLayout();

        cut.Markup.Should().Contain("page body").And.Contain("Simulation mode");
        cut.FindAll("a[href='simulator']").Should().ContainSingle();
    }

    [Fact]
    public void LiveEventsApi_ShouldShowConnectedWithoutSimulator()
    {
        Settings.Slack = new SlackSettings { SimulationMode = false };

        var cut = RenderLayout();

        cut.Markup.Should().Contain("Connected to Slack");
        cut.FindAll("a[href='simulator']").Should().BeEmpty();
    }

    [Theory]
    [InlineData(SlackConnectionState.Connected, "Connected to Slack")]
    [InlineData(SlackConnectionState.Error, "Slack: error")]
    [InlineData(SlackConnectionState.Connecting, "Slack: connecting")]
    public void SocketMode_ShouldShowRealConnectionState(SlackConnectionState state, string expected)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = "xapp" };
        Services.GetRequiredService<SlackConnectionStatus>().Set(state, "reason");

        var cut = RenderLayout();

        cut.Markup.Should().Contain(expected);
    }
}
