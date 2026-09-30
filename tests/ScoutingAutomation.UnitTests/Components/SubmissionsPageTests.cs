using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SubmissionsPageTests : PageTestBase
{
    private IRenderedComponent<Submissions> RenderList(Guid? group = null)
    {
        if (group.HasValue)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"submissions?group={group}");
        }

        var cut = Render<Submissions>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("submission"));
        return cut;
    }

    private static string[] FileNames(IRenderedComponent<Submissions> cut) =>
        cut.FindAll("tbody tr").Select(r => r.QuerySelector(".file-name a")!.TextContent.Trim()).ToArray();

    [Fact]
    public void List_ShouldShowAllUploadsNewestFirstWithDetails()
    {
        var seeded = Seed();

        var cut = RenderList();

        cut.WaitForAssertion(() => FileNames(cut).Should().Equal("raadsel.m4a", "DBU 1.pdf", "DWO 1.jpg", "DWO 2.mp4", "DWO 1.jpg"));
        cut.Find("h2").TextContent.Should().Contain("5 submissions");
        var rows = cut.FindAll("tbody tr");
        rows[0].TextContent.Should().Contain("Unresolved").And.Contain("No valid assignment number");
        rows[0].TextContent.Should().Contain("100 B");
        // Sizes follow the current culture (2.0 or 2,0)
        rows[1].TextContent.Should().MatchRegex(@"2[.,]0 KB");
        rows[3].TextContent.Should().MatchRegex(@"4[.,]8 MB");
        rows[2].TextContent.Should().Contain("vuur2.jpg", "the original name is shown when it differs");
        rows[2].QuerySelector("a[href^='groups/']")!.GetAttribute("href").Should().Be($"groups/{Wolves.Id}");
        rows[2].QuerySelector("a.assignment-badge")!.GetAttribute("href").Should().Be("assignments/1");
        _ = seeded;
    }

    [Fact]
    public void Filters_ShouldNarrowResultsAndClearFiltersShouldReset()
    {
        Seed();
        var cut = RenderList();
        cut.FindAll("tbody tr").Should().HaveCount(5);
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Contains("Clear filters"));

        var selects = cut.FindAll("select");
        selects[0].Change(Wolves.Id.ToString());
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(3));

        cut.FindAll("select")[1].Change("1");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));

        cut.FindAll("select")[2].Change(nameof(SubmissionStatus.Approved));
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(1));

        cut.Find("button.btn-scout-secondary").Click();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(5));
    }

    [Fact]
    public void SearchAndReviewerFilters_ShouldApplyWhileTyping()
    {
        Seed();
        var cut = RenderList();

        cut.FindAll("input[type=text]")[0].Input("knopen");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(1));

        cut.FindAll("input[type=text]")[0].Input("");
        cut.FindAll("input[type=text]")[1].Input("u_jury");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
    }

    [Fact]
    public void GroupFromQuery_ShouldPreselectGroup()
    {
        Seed();

        var cut = RenderList(Owls.Id);

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
        cut.FindAll("select")[0].GetAttribute("value").Should().Be(Owls.Id.ToString());
    }

    [Fact]
    public void NoMatches_ShouldShowEmptyState()
    {
        Seed();
        var cut = RenderList();

        cut.FindAll("input[type=text]")[0].Input("bestaat-niet");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No submissions match these filters."));
        cut.Find("h2").TextContent.Should().Contain("0 submissions");
    }

    [Fact]
    public void SingleResult_ShouldUseSingular()
    {
        WithDb(db =>
        {
            db.Groups.Add(Wolves);
            var upload = NewSubmission(Wolves, 1, SubmissionStatus.Pending, "a.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1);
            upload.StoragePath = "";
            upload.Group = null;
            db.Submissions.Add(upload);
            db.SaveChanges();
        });

        var cut = RenderList();

        cut.WaitForAssertion(() => cut.Find("h2").TextContent.Should().Contain("1 submission").And.NotContain("1 submissions"));
        FileNames(cut).Should().Equal("a.jpg");
    }
}
