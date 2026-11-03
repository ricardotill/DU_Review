using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Workflow;
using ScoutingAutomation.Web.Components.Shared;
using ScoutingAutomation.Web.Notifications;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// A group's bell menu for notices of newly opened assignments: the same choices as the jury's, kept apart from them,
/// plus a notification of the phone or computer. The group's toasts follow the choices.
/// </summary>
public class GroupNotificationTests : PageTestBase
{
    private GroupNotificationPreferences Preferences => Services.GetRequiredService<GroupNotificationPreferences>();

    /// <summary>The menu only shows in an interactive page.</summary>
    private IRenderedComponent<NotificationSettings> RenderMenu(bool forGroups)
    {
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
        return Render<NotificationSettings>(p => p.Add(c => c.ForGroups, forGroups));
    }

    private void Announce() => Services.GetRequiredService<ISubmissionChangeNotifier>()
        .NotifyUnlocked(new AssignmentsUnlockedNotification(Wolves.Id, ["31"], 31, 160));

    [Fact]
    public void GroupMenu_ShouldBeAboutAssignmentsAndStoreApartFromTheJury()
    {
        LogInAsGroup(Wolves);
        var cut = RenderMenu(forGroups: true);
        cut.WaitForAssertion(() => cut.Find(".bell-button").HasAttribute("disabled").Should().BeFalse());

        cut.Find(".bell-button").GetAttribute("title").Should().Be("Meldingen voor nieuwe opdrachten");
        cut.Find(".bell-button").Click();
        cut.Find(".menu-title").TextContent.Should().Be("Nieuwe opdrachten");
        cut.Find("#notify-system");

        cut.Find("#notify-sound").Change(true);

        var saved = JSInterop.VerifyInvoke("duNotifications.setPrefs");
        saved.Arguments[1].Should().Be("du_notifications_group");
        Services.GetRequiredService<NotificationPreferences>().SoundEnabled.Should().BeFalse("the jury's choice is not touched");
    }

    [Fact]
    public void JuryMenu_ShouldHaveNoSystemSwitch()
    {
        var cut = RenderMenu(forGroups: false);
        cut.WaitForAssertion(() => cut.Find(".bell-button").HasAttribute("disabled").Should().BeFalse());

        cut.Find(".bell-button").Click();

        cut.Find(".menu-title").TextContent.Should().Be("Nieuwe inzendingen");
        cut.FindAll("#notify-system").Should().BeEmpty();
    }

    [Fact]
    public void SystemSwitch_ShouldAskTheBrowserAndStayOffWhenRefused()
    {
        JSInterop.Setup<string>("duNotifications.systemPermission").SetResult("default");
        JSInterop.Setup<string>("duNotifications.requestSystemPermission").SetResult("denied");
        LogInAsGroup(Wolves);
        var cut = RenderMenu(forGroups: true);
        cut.WaitForAssertion(() => cut.Find(".bell-button").HasAttribute("disabled").Should().BeFalse());
        cut.Find(".bell-button").Click();

        cut.Find("#notify-system").Change(true);

        Preferences.SystemEnabled.Should().BeFalse();
        cut.Find(".system-denied");
        cut.Find("#notify-system").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Toasts_TurnedOff_ShouldShowNothing()
    {
        LogInAsGroup(Wolves);
        var cut = Render<GroupToastHost>();
        await cut.InvokeAsync(() => Preferences.SetToastsEnabledAsync(false));

        Announce();

        cut.FindAll(".unlock-toast").Should().BeEmpty();
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "duNotifications.systemNotify");
    }

    [Fact]
    public async Task SystemNotification_WhenChosen_ShouldBeShownWithTheToast()
    {
        JSInterop.Setup<string>("duNotifications.systemPermission").SetResult("granted");
        LogInAsGroup(Wolves);
        var cut = Render<GroupToastHost>();
        await cut.InvokeAsync(() => Preferences.SetSystemEnabledAsync(true));

        Announce();

        cut.WaitForAssertion(() => cut.Find(".unlock-toast"));
        cut.WaitForAssertion(() => JSInterop.VerifyInvoke("duNotifications.systemNotify"));
    }
}
