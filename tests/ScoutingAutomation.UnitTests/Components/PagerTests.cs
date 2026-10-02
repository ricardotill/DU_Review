using Bunit;
using FluentAssertions;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class PagerTests : BunitContext
{
    private IRenderedComponent<Pager> RenderPager(int total, int page, Action<int>? changed = null) =>
        Render<Pager>(p => p
            .Add(c => c.TotalCount, total)
            .Add(c => c.PageSize, 10)
            .Add(c => c.Page, page)
            .Add(c => c.PageChanged, changed ?? (_ => { })));

    private static IReadOnlyList<string> Labels(IRenderedComponent<Pager> cut) =>
        cut.FindAll("nav button, nav span.px-1").Select(e => e.TextContent.Trim()).ToList();

    [Fact]
    public void OnePage_ShouldRenderNothing()
    {
        RenderPager(10, 1).Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void ManyPages_ShouldShowFirstLastAndNeighboursWithGaps()
    {
        var cut = RenderPager(200, 10);

        Labels(cut).Should().Equal("", "1", "…", "8", "9", "10", "11", "12", "…", "20", "");
        cut.Find("button[aria-current=page]").TextContent.Should().Be("10");
        cut.Markup.Should().Contain("91–100 van 200");
    }

    [Fact]
    public void LastPage_ShouldCountRemainderAndDisableNext()
    {
        var cut = RenderPager(25, 3);

        cut.Markup.Should().Contain("21–25 van 25");
        cut.Find("button[aria-label=Volgende]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button[aria-label=Vorige]").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void PageBeyondEnd_ShouldShowLastPage()
    {
        var cut = RenderPager(25, 9);

        cut.Find("button[aria-current=page]").TextContent.Should().Be("3");
    }

    [Fact]
    public void Clicks_ShouldReportTheChosenPage()
    {
        var chosen = new List<int>();
        var cut = RenderPager(50, 2, chosen.Add);

        cut.Find("button[aria-label=Volgende]").Click();
        cut.Find("button[aria-label=Vorige]").Click();
        cut.FindAll("nav button").Single(b => b.TextContent == "5").Click();

        chosen.Should().Equal(3, 1, 5);
    }

    [Fact]
    public void Slice_ShouldTakeThePageAndFallBackToTheLastOne()
    {
        var items = Enumerable.Range(1, 25).ToList();

        Pager.Slice(items, 2, 10).Should().Equal(Enumerable.Range(11, 10));
        Pager.Slice(items, 7, 10).Should().Equal(21, 22, 23, 24, 25);
        Pager.Slice(new List<int>(), 3, 10).Should().BeEmpty();
    }
}
