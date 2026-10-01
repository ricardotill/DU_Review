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
        cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("De Vossen")).TextContent.Should().Contain("niet ingesteld").And.Contain("–");
        cut.Find("a[href^='groups/']").GetAttribute("href").Should().StartWith("groups/");
        cut.Markup.Should().Contain("U_JURY").And.Contain("Alleen reacties van deze Slack-gebruikers tellen");
        cut.Markup.Should().Contain(":thumbsup:").And.Contain("/approved/").And.Contain("/awesome/").And.Contain("/rejected/");
        cut.Markup.Should().Contain("geen emoji").And.Contain("Geen antwoord").And.Contain(":question:");
        cut.Markup.Should().Contain(".jpg").And.Contain("image/*");
        Row(cut, "Opdrachten").Should().Contain("3 geladen");
        Row(cut, "Slack-modus").Should().Contain("Simulatie");
        Row(cut, "Signing secret").Should().Contain("niet gecontroleerd in simulatie");
        Row(cut, "Bot token").Should().Contain("niet ingesteld");
        cut.FindAll("dt").Select(d => d.TextContent).Should().NotContain("Verbinding");
    }

    [Fact]
    public void NoReviewers_ShouldWarnThatReactionsAreIgnored()
    {
        Settings.Reviewers = new List<string>();

        var cut = Render<Configuration>();

        cut.Markup.Should().Contain("Geen juryleden: alle reacties worden genegeerd.");
    }

    [Theory]
    [InlineData("", "niet ingesteld: alle verzoeken van Slack worden geweigerd")]
    [InlineData("secret", "ingesteld, verzoeken worden gecontroleerd")]
    public void EventsApi_ShouldShowSigningSecretState(string secret, string expected)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SigningSecret = secret, BotToken = "xoxb" };

        var cut = Render<Configuration>();

        Row(cut, "Slack-modus").Should().Contain("Live");
        Row(cut, "Verbinding").Should().Contain("Events API");
        Row(cut, "Signing secret").Should().Contain(expected);
        Row(cut, "Bot token").Should().Contain("ingesteld");
        cut.FindAll("dt").Select(d => d.TextContent).Should().NotContain("App token");
    }

    [Theory]
    [InlineData("", SlackConnectionState.Error, "App-token niet ingesteld", "badge-rejected", "niet ingesteld: er komen geen gebeurtenissen binnen")]
    [InlineData("xapp", SlackConnectionState.Connected, null, "badge-approved", "ingesteld")]
    [InlineData("xapp", SlackConnectionState.Connecting, null, "badge-pending", "ingesteld")]
    [InlineData("xapp", SlackConnectionState.Disabled, null, "badge-received", "ingesteld")]
    public void SocketMode_ShouldShowConnectionState(string appToken, SlackConnectionState state, string? detail, string badge, string tokenText)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = appToken };
        Services.GetRequiredService<SlackConnectionStatus>().Set(state, detail);

        var cut = Render<Configuration>();

        var connection = cut.FindAll("dt").Single(dt => dt.TextContent == "Verbinding").NextElementSibling!;
        connection.TextContent.Should().Contain("Socket Mode").And.Contain(state.Label());
        connection.QuerySelector(".badge")!.ClassList.Should().Contain(badge);
        if (detail != null) connection.TextContent.Should().Contain(detail);
        Row(cut, "App token").Should().Contain(tokenText);
        Row(cut, "Signing secret").Should().Contain("niet nodig bij Socket Mode");
    }
}

public class LayoutTests : PageTestBase
{
    private IRenderedComponent<MainLayout> RenderLayout() =>
        RenderLayout("page body");

    [Fact]
    public void Simulation_ShouldShowSimulationBadgeAndSimulatorLink()
    {
        var cut = RenderLayout();

        cut.Markup.Should().Contain("page body").And.Contain("Simulatiemodus");
        cut.FindAll("a[href='simulator']").Should().ContainSingle();
    }

    [Fact]
    public void LiveEventsApi_ShouldShowConnectedWithoutSimulator()
    {
        Settings.Slack = new SlackSettings { SimulationMode = false };

        var cut = RenderLayout();

        cut.Markup.Should().Contain("Verbonden met Slack");
        cut.FindAll("a[href='simulator']").Should().BeEmpty();
    }

    [Theory]
    [InlineData(SlackConnectionState.Connected, "Verbonden met Slack")]
    [InlineData(SlackConnectionState.Error, "Slack: fout")]
    [InlineData(SlackConnectionState.Connecting, "Slack: verbinden")]
    public void SocketMode_ShouldShowRealConnectionState(SlackConnectionState state, string expected)
    {
        Settings.Slack = new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = "xapp" };
        Services.GetRequiredService<SlackConnectionStatus>().Set(state, "reason");

        var cut = RenderLayout();

        cut.Markup.Should().Contain(expected);
    }
}
