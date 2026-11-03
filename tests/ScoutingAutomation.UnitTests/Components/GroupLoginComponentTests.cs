using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Workflow;
using ScoutingAutomation.Web.Components.Layout;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>What a group login sees: its own data, the standing unless hidden, and nothing of the jury.</summary>
public class GroupLoginComponentTests : PageTestBase
{
    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    private IRenderedComponent<Home> RenderGroupDashboard()
    {
        Seed();
        LogInAsGroup(Wolves);
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".assignment-results tbody tr").Should().HaveCount(2));
        return cut;
    }

    [Fact]
    public void Dashboard_ShouldShowOwnResultsAndTheStanding()
    {
        var cut = RenderGroupDashboard();

        cut.Find("h1").TextContent.Should().Be("De Wolven");
        cut.Find(".own-score").TextContent.Should().Be("5");
        var standing = cut.FindAll(".standing-table tbody tr");
        standing.Should().HaveCount(3);
        standing[0].ClassList.Should().Contain("own-group");
        standing[0].TextContent.Should().Contain("De Wolven");
        cut.FindAll(".assignment-results .points").Select(p => p.TextContent.Trim()).Should().Equal("+1", "+4");

        // Nothing of the jury dashboard and no links to pages for the jury
        cut.FindAll(".leaderboard").Should().BeEmpty();
        cut.FindAll("a[href^='groups']").Should().BeEmpty();
        cut.FindAll("a[href^='assignments']").Should().BeEmpty();
        cut.FindAll("a[href^='/api/export']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Handmatige modus");
        cut.Markup.Should().NotContain("knopen.pdf", "uploads of other groups are never loaded");
    }

    [Fact]
    public void HiddenStanding_ShouldStillShowOwnScoreAndPointsButNoRanksOrOthers()
    {
        Runtime.SetStandingHidden(true);

        var cut = RenderGroupDashboard();

        cut.Find(".own-score").TextContent.Should().Be("5");
        cut.Find(".own-rank").TextContent.Should().Be("Plaats tijdelijk verborgen");
        cut.FindAll(".assignment-results .points").Select(p => p.TextContent.Trim()).Should().Equal("+1", "+4");
        cut.FindAll(".assignment-results .badge-status").Should().NotBeEmpty("the review status stays visible");
        cut.FindAll(".standing-table").Should().BeEmpty();
        cut.FindAll(".score-chart").Should().BeEmpty();
        cut.Find(".standing-hidden").TextContent.Should().Contain("tijdelijk verborgen");
        cut.Markup.Should().NotContain("Plaats 1").And.NotContain("De Bosuilen");
    }

    [Fact]
    public void HiddenStanding_WithInterimStanding_ShouldShowThatInsteadOfTheLiveOne()
    {
        Runtime.PublishInterimStanding(
            [new InterimStandingRow(Owls.Id, "De Bosuilen", 1, 3), new InterimStandingRow(Wolves.Id, "De Wolven", 2, 1)],
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        Runtime.SetStandingHidden(true);

        var cut = RenderGroupDashboard();

        cut.Find(".interim-note").TextContent.Should().Contain("tussenstand van 14:00");
        var rows = cut.FindAll(".standing-table tbody tr");
        rows.Select(r => r.TextContent).Should().SatisfyRespectively(
            first => first.Should().Contain("De Bosuilen").And.Contain("3"),
            second => second.Should().Contain("De Wolven").And.Contain("1"));
        rows[1].ClassList.Should().Contain("own-group");
    }

    [Fact]
    public void InterimStanding_ShouldNotShowWhileTheStandingIsVisible()
    {
        Runtime.PublishInterimStanding([new InterimStandingRow(Wolves.Id, "De Wolven", 1, 99)], DateTimeOffset.UtcNow);

        var cut = RenderGroupDashboard();

        cut.FindAll(".interim-note").Should().BeEmpty();
        cut.Find(".own-score").TextContent.Should().Be("5");
    }

    [Fact]
    public void Dashboard_ShouldFollowTheJuryHidingAndShowingTheStanding()
    {
        var cut = RenderGroupDashboard();

        Runtime.SetStandingHidden(true);
        cut.WaitForAssertion(() => cut.FindAll(".standing-table").Should().BeEmpty());

        Runtime.SetStandingHidden(false);
        cut.WaitForAssertion(() => cut.FindAll(".standing-table tbody tr").Should().HaveCount(3));
    }

    [Fact]
    public void HidePeriod_ShouldHideTheStandingWhileItRuns()
    {
        var now = DateTimeOffset.UtcNow.ToGameTime().DateTime;
        Runtime.AddStandingHideWindow(now.AddHours(-1), now.AddHours(1)).Should().BeNull();

        var cut = RenderGroupDashboard();

        cut.FindAll(".standing-table").Should().BeEmpty();
    }

    [Fact]
    public void Submissions_ShouldOnlyListOwnUploadsWhateverIsAsked()
    {
        Seed();
        LogInAsGroup(Wolves);
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        navigation.NavigateTo($"submissions?group={Owls.Id}");

        var cut = Render<Submissions>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(3));
        cut.Markup.Should().NotContain("knopen.pdf").And.NotContain("raadsel.m4a");
        cut.Find("h1").TextContent.Should().Be("Onze inzendingen");
        cut.FindAll("select option").Select(o => o.TextContent).Should().NotContain("De Bosuilen", "a group does not get a group filter");
        cut.Markup.Should().NotContain("Jurylid");
        cut.FindAll(".slideshow-link").Should().BeEmpty();
        cut.FindAll("a[href^='groups']").Should().BeEmpty();
        cut.FindAll("a[href^='assignments']").Should().BeEmpty();
    }

    [Fact]
    public void SubmissionDetail_OfAnotherGroup_ShouldLookNotFound()
    {
        var seeded = Seed();
        LogInAsGroup(Wolves);

        var cut = Render<SubmissionDetail>(p => p.Add(c => c.Id, seeded.Pending.Id));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Inzending niet gevonden"));
        cut.Markup.Should().NotContain("knopen.pdf");
    }

    [Fact]
    public void SubmissionDetail_OfOwnGroup_ShouldHideWhoReviewedAndWhereItIsStored()
    {
        var seeded = Seed();
        LogInAsGroup(Wolves);

        var cut = Render<SubmissionDetail>(p => p.Add(c => c.Id, seeded.Rejected.Id));

        cut.WaitForAssertion(() => cut.FindAll(".timeline-item").Should().HaveCount(1));
        cut.Find(".timeline-item").TextContent.Should().Contain("Jury").And.NotContain("U_JURY").And.NotContain("Te donker");
        cut.Markup.Should().NotContain(seeded.Rejected.StoragePath);
        cut.Markup.Should().NotContain(seeded.Rejected.SlackMessageId);
        cut.Markup.Should().NotContain("Status handmatig aanpassen");
        cut.FindAll("a[href^='groups']").Should().BeEmpty();
    }

    [Fact]
    public void NavMenu_ShouldOnlyOfferTheGroupPages()
    {
        LogInAsGroup(Wolves);

        var cut = Render<NavMenu>();

        cut.FindAll("a.nav-link").Select(a => a.GetAttribute("href")).Should().Equal("", "submissions", "edities");
        cut.Markup.Should().Contain("De Wolven");
    }

    [Fact]
    public void NavMenu_ForTheJury_ShouldOfferTheStandingPage()
    {
        LogInAsJury();

        var cut = Render<NavMenu>();

        cut.FindAll("a.nav-link").Select(a => a.GetAttribute("href")).Should().Contain(["queue", "standing", "scoreboard"]);
    }

    [Fact]
    public void Layout_ForAGroup_ShouldNotShowSlackOrModes()
    {
        Runtime.SetManualMode(true);
        LogInAsGroup(Wolves);

        var cut = RenderLayout("body");

        cut.Markup.Should().NotContain("Simulatiemodus").And.NotContain("Handmatige modus").And.NotContain("Verbonden met Slack");
    }

    private void AnnounceOwlsUpload() =>
        Services.GetRequiredService<ISubmissionChangeNotifier>().NotifyCreated(
            new SubmissionCreatedNotification(Owls.Id, Owls.Name, Owls.Abbreviation, "7", "Scout Uil", [Guid.NewGuid()], DateTimeOffset.UtcNow));

    [Fact]
    public void Layout_ShouldNotShowAGroupTheToastsOfOtherGroups()
    {
        LogInAsGroup(Wolves);

        var cut = RenderLayout("body");
        AnnounceOwlsUpload();

        cut.FindAll(".toast-stack").Should().BeEmpty();
        cut.Markup.Should().NotContain("De Bosuilen");
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void ToastHost_ShouldOnlyAnnounceUploadsToTheJury(bool jury, int expectedToasts)
    {
        // Checked by the toasts themselves too, not only by the layout around them
        if (jury) LogInAsJury();
        else LogInAsGroup(Wolves);

        var cut = Render<ToastHost>();
        AnnounceOwlsUpload();

        cut.WaitForAssertion(() => cut.FindAll(".submission-toast").Should().HaveCount(expectedToasts));
        if (!jury) cut.Markup.Should().NotContain("De Bosuilen");
    }

    [Fact]
    public void JuryDashboard_ShouldStayLiveAndSayTheStandingIsHidden()
    {
        Seed();
        Runtime.SetStandingHidden(true);
        LogInAsJury();

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.FindAll(".leaderboard tbody tr").Should().HaveCount(3));
        cut.Find(".standing-hidden-banner").TextContent.Should().Contain("verborgen voor groepen").And.Contain("geen tussenstand");
    }
}
