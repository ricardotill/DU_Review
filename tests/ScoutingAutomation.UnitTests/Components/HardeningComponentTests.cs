using System.Security.Claims;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Auth;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>Pages that keep working when something fails, and admin actions checked on the server.</summary>
public class HardeningComponentTests : PageTestBase
{
    private SlackEventLog Log => Services.GetRequiredService<SlackEventLog>();

    private static Task<AuthenticationState> State(params string[] roles) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            roles.Select(r => new Claim(ClaimTypes.Role, r)).Append(new Claim(ClaimTypes.Name, "x")), "test"))));

    [Fact]
    public async Task AdminAction_FromATabThatIsNoLongerAdmin_ShouldNotRun()
    {
        var ran = false;

        var error = await AdminActions.RunAsync(State(), () => { ran = true; });

        ran.Should().BeFalse();
        error.Should().Be(AdminActions.NotAdmin);
    }

    [Fact]
    public async Task AdminAction_ThatCannotBeSaved_ShouldShowAMessageInsteadOfBreakingThePage()
    {
        var error = await AdminActions.RunAsync(State(KeyAuth.AdminRole), () => throw new IOException("No space left on device"));

        error.Should().Contain("No space left on device");
    }

    [Fact]
    public void Events_ConnectionDrop_ShouldShowAsABannerUntilConnectedAgain()
    {
        var cut = Render<Events>();

        Log.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Failed, "Verbinding met Slack mislukt of verbroken: timeout", "Slack Socket Mode");

        cut.WaitForAssertion(() => cut.Find(".connection-problem").TextContent.Should().Contain("timeout"));
        var row = cut.Find("tbody tr");
        row.TextContent.Should().Contain("Verbinding").And.Contain("Mislukt").And.Contain("–");
        row.ClassList.Should().Contain("event-failed");

        Log.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Processed, "Verbonden met Slack", "Slack Socket Mode");

        cut.WaitForAssertion(() => cut.FindAll(".connection-problem").Should().BeEmpty());
    }

    [Fact]
    public void Events_ErrorsAndCatchUps_ShouldBeListedAndFilterable()
    {
        Log.AddNotice(SlackEventKind.System, SlackEventOutcome.Failed, "Posting the game-start message failed: disk full", "GameStartAnnouncer");
        Log.AddNotice(SlackEventKind.CatchUp, SlackEventOutcome.Processed, "Niets gemist in 3 bericht(en)", "Bijwerken uit Slack");
        Log.AddNotice(SlackEventKind.Connection, SlackEventOutcome.Ignored, "Slack vroeg om opnieuw te verbinden; opnieuw verbinden", "Slack Socket Mode");
        var cut = Render<Events>();

        cut.FindAll("tbody tr").Should().HaveCount(3);
        cut.Markup.Should().Contain("Fout in de app").And.Contain("Bijwerken").And.Contain("Info");

        cut.FindAll("select")[0].Change(nameof(SlackEventKind.System));

        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("disk full");
    }

    [Fact]
    public void Configuration_ForTheJury_ShouldNotShowWhereTheServerKeepsItsFiles()
    {
        Seed();
        LogInAsJury();

        var cut = Render<Configuration>();

        cut.Markup.Should().NotContain(StorageRoot);
    }

    [Fact]
    public void Configuration_ForAnAdmin_ShouldShowTheFullStoragePath()
    {
        Seed();

        var cut = Render<Configuration>();

        cut.Markup.Should().Contain(StorageRoot);
    }
}
