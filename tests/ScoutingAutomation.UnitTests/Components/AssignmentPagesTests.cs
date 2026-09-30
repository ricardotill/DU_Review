using Bunit;
using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class AssignmentsPageTests : PageTestBase
{
    [Fact]
    public void List_ShouldShowOnlyAssignmentsWithSubmissionsByDefault()
    {
        Seed();

        var cut = Render<Assignments>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        var first = cut.FindAll("tbody tr")[0].QuerySelectorAll("td");
        first[0].TextContent.Trim().Should().Be("#1");
        first[1].TextContent.Should().Contain("1").And.Contain("2");
        first[2].TextContent.Should().Contain("2").And.Contain("/ 3");
    }

    [Fact]
    public void Toggle_ShouldShowAllAssignments()
    {
        Seed();
        var cut = Render<Assignments>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find("#onlySubmitted").Change(false);

        cut.FindAll("tbody tr").Should().HaveCount(3);
    }

    [Fact]
    public void NumberSearch_ShouldFilterByPrefix()
    {
        Seed();
        var cut = Render<Assignments>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.Find("input[type=number]").Input("2");

        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("#2");

        cut.Find("input[type=number]").Input("9");
        cut.Markup.Should().Contain("No assignments match.");
    }

    [Fact]
    public void ClickingRow_ShouldOpenAssignment()
    {
        Seed();
        var cut = Render<Assignments>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.FindAll("tbody tr")[1].Click();

        CurrentUri.Should().EndWith("assignments/2");
    }

    [Fact]
    public void SubmissionForUnlistedAssignment_ShouldShowZeroPoints()
    {
        WithDb(db =>
        {
            db.Groups.Add(Wolves);
            db.Submissions.Add(NewSubmission(Wolves, 42, SubmissionStatus.Pending, "a.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1));
            db.SaveChanges();
        });

        var cut = Render<Assignments>();

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().ContainSingle());
        cut.Find("tbody tr td:nth-child(2)").TextContent.Should().MatchRegex(@"0\s*/\s*0");
    }
}

public class AssignmentDetailPageTests : PageTestBase
{
    private IRenderedComponent<AssignmentDetail> RenderAssignment(int number)
    {
        var cut = Render<AssignmentDetail>(p => p.Add(c => c.Number, number));
        cut.WaitForAssertion(() => cut.FindAll(".spinner-border").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public void Assignment_ShouldCompareGroupsAndMarkTheCountingSubmission()
    {
        var seeded = Seed();

        var cut = RenderAssignment(1);

        cut.Find("h1").TextContent.Should().Be("Assignment #1");
        cut.Markup.Should().Contain("1 pts approved").And.Contain("2 pts awesome").And.Contain("2 / 3 groups submitted");

        var cards = cut.FindAll(".col-xl-6");
        cards.Select(c => c.QuerySelector("h2")!.TextContent.Trim()).Should().Equal("De Bosuilen", "De Wolven");

        var wolves = cards[1];
        wolves.TextContent.Should().Contain("+1");
        var rows = wolves.QuerySelectorAll("a.submission-row");
        rows.Should().HaveCount(2);
        rows[0].GetAttribute("href").Should().Be($"submissions/{seeded.Approved.Id}");
        rows[0].ClassList.Should().Contain("counts");
        rows[0].TextContent.Should().Contain("counts");
        rows[0].QuerySelector("img").Should().NotBeNull("images get a thumbnail");

        var owls = cards[0];
        owls.QuerySelectorAll("a.submission-row .counts, span.text-success").Should().BeEmpty("a single upload needs no 'counts' label");
        owls.QuerySelector(".thumb i").Should().NotBeNull("a PDF gets an icon");

        cut.FindAll("a.badge.me-1").Select(a => a.TextContent).Should().Equal("De Vossen");
    }

    [Fact]
    public void PreviousAndNext_ShouldFollowTheAssignmentList()
    {
        Seed();

        var first = RenderAssignment(1);
        first.Find("a[aria-label='Previous assignment']").ClassList.Should().Contain("disabled");
        first.Find("a[aria-label='Next assignment']").GetAttribute("href").Should().Be("assignments/2");

        var last = RenderAssignment(3);
        last.Find("a[aria-label='Previous assignment']").GetAttribute("href").Should().Be("assignments/2");
        last.Find("a[aria-label='Next assignment']").ClassList.Should().Contain("disabled");
        last.Markup.Should().Contain("No group has submitted this assignment yet.");
    }

    [Fact]
    public void NavigatingToAnotherAssignment_ShouldReload()
    {
        Seed();
        var cut = RenderAssignment(1);

        cut.Render(p => p.Add(c => c.Number, 2));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Assignment #2"));
        cut.FindAll(".col-xl-6").Should().ContainSingle();
    }

    [Fact]
    public void UnknownAssignment_ShouldSayItIsNotListed()
    {
        Seed();

        var cut = RenderAssignment(999);

        cut.Markup.Should().Contain("Not in the assignment list");
    }

    [Fact]
    public void LiveUpdate_ShouldNotShowSpinnerAgain()
    {
        Seed();
        var cut = RenderAssignment(3);

        WithDb(db =>
        {
            db.Submissions.Add(NewSubmission(Foxes, 3, SubmissionStatus.Pending, "x.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1));
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.FindAll(".col-xl-6").Should().ContainSingle(), TimeSpan.FromSeconds(5));
    }
}
