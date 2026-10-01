using Bunit;
using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class GroupsPageTests : PageTestBase
{
    [Fact]
    public void Groups_ShouldBeListedByRankWithScores()
    {
        Seed();

        var cut = Render<Groups>();

        cut.WaitForAssertion(() => cut.FindAll("a.group-card").Should().HaveCount(3));
        var cards = cut.FindAll("a.group-card");
        cards[0].TextContent.Should().Contain("De Wolven").And.Contain("#1").And.Contain("5").And.Contain("2 opdrachten ingezonden");
        cards[1].TextContent.Should().Contain("De Bosuilen").And.Contain("1 opdracht ingezonden");
        cards[2].GetAttribute("href").Should().Be($"groups/{Foxes.Id}");
        cards[2].QuerySelector(".font-monospace").Should().BeNull();
    }

    [Fact]
    public void NoGroups_ShouldShowEmptyState()
    {
        var cut = Render<Groups>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Geen groepen ingesteld."));
    }
}

public class GroupDetailPageTests : PageTestBase
{
    private IRenderedComponent<GroupDetail> RenderGroup(Guid id)
    {
        var cut = Render<GroupDetail>(p => p.Add(c => c.Id, id));
        cut.WaitForAssertion(() => cut.FindAll(".spinner-border").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public void Group_ShouldOfferGroupRebuildToAdminsOnly()
    {
        Seed();

        RenderGroup(Wolves.Id).Markup.Should().Contain("Opnieuw opbouwen uit Slack");

        LogInAsJury();
        RenderGroup(Wolves.Id).Markup.Should().NotContain("Opnieuw opbouwen uit Slack");
    }

    [Fact]
    public void Group_ShouldShowScoreAssignmentsAndLinks()
    {
        var seeded = Seed();

        var cut = RenderGroup(Wolves.Id);

        cut.Find("h1").TextContent.Should().Be("De Wolven");
        cut.Markup.Should().Contain("Welpen horde").And.Contain("DWO").And.Contain("#C1").And.Contain("Plaats 1 van 3").And.Contain("van 3 opdrachten");
        cut.Find("a[href^='submissions?group=']").GetAttribute("href").Should().Be($"submissions?group={Wolves.Id}");
        cut.FindAll(".unresolved-panel").Should().BeEmpty();

        var rows = cut.FindAll("tbody tr");
        rows.Should().HaveCount(2);
        rows[0].TextContent.Should().Contain("#1").And.Contain("+1").And.Contain("Goedgekeurd");
        rows[0].QuerySelectorAll("td")[3].TextContent.Should().Be("2");
        rows[0].QuerySelector("a.btn")!.GetAttribute("href").Should().Be($"submissions/{seeded.Approved.Id}");
        rows[1].TextContent.Should().Contain("+4");
    }

    [Fact]
    public void GroupWithUnresolvedAndPending_ShouldListThem()
    {
        var seeded = Seed();

        var cut = RenderGroup(Owls.Id);

        cut.Find(".unresolved-panel").TextContent.Should().Contain("Uploads zonder opdracht (1)").And.Contain("raadsel.m4a");
        cut.Find(".unresolved-panel a").GetAttribute("href").Should().Be($"submissions/{seeded.Unresolved.Id}");
        cut.Find("tbody tr td:nth-child(3)").TextContent.Trim().Should().Be("0");
    }

    [Fact]
    public void SeveralUnresolvedUploads_ShouldBeNewestFirst()
    {
        Seed();
        WithDb(db =>
        {
            var newer = NewSubmission(Owls, null, SubmissionStatus.Pending, "nieuwer.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1);
            newer.UnresolvedReason = "geen nummer";
            db.Submissions.Add(newer);
            db.SaveChanges();
        });

        var cut = RenderGroup(Owls.Id);

        cut.Find(".unresolved-panel").TextContent.Should().Contain("(2)");
        cut.FindAll(".unresolved-panel a .fw-semibold").Select(a => a.TextContent).Should().Equal("nieuwer.jpg", "raadsel.m4a");
    }

    [Fact]
    public void GroupWithoutUploads_ShouldShowEmptyState()
    {
        Seed();

        var cut = RenderGroup(Foxes.Id);

        cut.Markup.Should().Contain("Deze groep heeft nog niets ingezonden.");
        cut.FindAll("h1 + p").Should().BeEmpty("the Foxes have no description");
    }

    [Fact]
    public void GroupWithoutStatistics_ShouldShowZeros()
    {
        // A group only in the database, not yet part of the statistics run
        var cut = RenderGroup(Guid.NewGuid());

        cut.Find("h1").TextContent.Should().Be("Groep niet gevonden");
    }

    [Fact]
    public void SubmissionForUnlistedAssignment_ShouldEarnNothing()
    {
        WithDb(db =>
        {
            db.Groups.Add(Wolves);
            db.Submissions.Add(NewSubmission(Wolves, 42, SubmissionStatus.Approved, "a.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1));
            db.SaveChanges();
        });

        var cut = RenderGroup(Wolves.Id);

        cut.Find("tbody tr td:nth-child(3)").TextContent.Trim().Should().Be("0");
    }
}
