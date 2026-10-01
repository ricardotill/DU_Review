using Bunit;
using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class HomePageTests : PageTestBase
{
    [Fact]
    public void Dashboard_ShouldShowTotalsLeaderboardAndActiveAssignments()
    {
        Seed();

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.FindAll(".leaderboard tbody tr").Should().HaveCount(3));
        var stats = cut.FindAll(".stat-card").ToDictionary(c => c.QuerySelector(".stat-label")!.TextContent, c => c.QuerySelector(".stat-value")!.TextContent);
        stats["Ingezonden"].Should().Be("3");
        stats["Goedgekeurd"].Should().Be("1");
        stats["Awesome"].Should().Be("1");
        stats["Open"].Should().Be("1");
        stats["Afgekeurd"].Should().Be("0", "the rejected upload was replaced by an approved one");
        stats["Onopgelost"].Should().Be("1");

        var rows = cut.FindAll(".leaderboard tbody tr");
        rows[0].TextContent.Should().Contain("De Wolven").And.Contain("DWO");
        var wolvesCells = rows[0].QuerySelectorAll("td");
        wolvesCells[0].TextContent.Should().Be("1");
        wolvesCells[2].TextContent.Should().Be("5", "approved #1 (1) + awesome #2 (4)");
        wolvesCells[3].TextContent.Should().Be("2");
        rows[2].TextContent.Should().Contain("De Vossen");
        rows[2].QuerySelectorAll(".badge.font-monospace").Should().BeEmpty("the Foxes have no abbreviation");

        cut.FindAll(".assignment-card").Select(c => c.GetAttribute("href")).Should().Equal("assignments/1", "assignments/2");
    }

    [Fact]
    public void ClickingLeaderboardRow_ShouldOpenGroup()
    {
        Seed();
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".leaderboard tbody tr").Should().HaveCount(3));

        cut.FindAll(".leaderboard tbody tr")[0].Click();

        CurrentUri.Should().EndWith($"groups/{Wolves.Id}");
    }

    [Fact]
    public void EmptyEvent_ShouldShowEmptyStates()
    {
        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Geen groepen ingesteld."));
        cut.Markup.Should().Contain("Nog geen inzendingen.");
        cut.FindAll(".stat-label").Select(l => l.TextContent).Should().NotContain("Onopgelost");
    }

    [Fact]
    public void NewSubmission_ShouldAppearWithoutReload()
    {
        Seed();
        var cut = Render<Home>();
        cut.WaitForAssertion(() => cut.FindAll(".assignment-card").Should().HaveCount(2));

        WithDb(db =>
        {
            db.Submissions.Add(NewSubmission(Foxes, 3, SubmissionStatus.Pending, "nieuw.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1));
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.FindAll(".assignment-card").Should().HaveCount(3), TimeSpan.FromSeconds(5));
    }
}
