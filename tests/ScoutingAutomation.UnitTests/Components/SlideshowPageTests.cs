using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Favorites;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SlideshowPageTests : PageTestBase
{
    private void CookieHas(params Domain.Entities.Submission[] favorites) =>
        JSInterop.Setup<string>("duFavorites.get").SetResult(string.Join('.', favorites.Select(FavoritesService.KeyFor)));

    private IRenderedComponent<Slideshow> RenderSlideshow(string? query = null)
    {
        if (query != null)
        {
            Services.GetRequiredService<NavigationManager>().NavigateTo($"slideshow?{query}");
        }

        var cut = Render<Slideshow>();
        cut.WaitForAssertion(() => cut.Find("#slideshowFavorites").HasAttribute("disabled").Should().BeFalse());
        return cut;
    }

    private static void Start(IRenderedComponent<Slideshow> cut) =>
        cut.FindAll("button").First(b => b.TextContent.Contains("Diavoorstelling starten")).Click();

    private static string Caption(IRenderedComponent<Slideshow> cut) => cut.Find(".caption").TextContent;

    [Fact]
    public void Setup_ShouldCountOnlyMediaThatIsNotRejected()
    {
        Seed();

        var cut = RenderSlideshow();

        // Approved photo, awesome video and the unresolved audio; not the rejected photo or the PDF
        cut.Find(".item-count").TextContent.Should().Be("3 items");
    }

    [Fact]
    public void Show_ShouldPlayOldestFirstAndShowAssignmentAndGroup()
    {
        var seeded = Seed();
        var cut = RenderSlideshow();

        Start(cut);

        cut.Find(".media video").GetAttribute("src").Should().Be($"/api/files/{seeded.Awesome.Id}");
        Caption(cut).Should().Contain("Opdracht 2").And.Contain("De Wolven");
        cut.Find(".position").TextContent.Should().Be("1 / 3");

        cut.Find("button[aria-label=Volgende]").Click();
        cut.Find(".media img").GetAttribute("src").Should().Be($"/api/files/{seeded.Approved.Id}");
        Caption(cut).Should().Contain("Opdracht 1");

        cut.Find("button[aria-label=Volgende]").Click();
        cut.Find(".media audio").GetAttribute("src").Should().Be($"/api/files/{seeded.Unresolved.Id}");
        cut.Find(".audio-card").TextContent.Should().Contain("De Bosuilen").And.Contain("Onbekende opdracht");
    }

    [Fact]
    public void Loop_ShouldWrapAroundAndCanBeTurnedOff()
    {
        var seeded = Seed();
        var cut = RenderSlideshow();
        Start(cut);

        cut.Find("button[aria-label=Vorige]").Click();
        cut.Find(".position").TextContent.Should().Be("3 / 3");
        cut.Find("button[aria-label=Volgende]").Click();
        cut.Find(".media video").GetAttribute("src").Should().Be($"/api/files/{seeded.Awesome.Id}");

        cut.Find(".loop-toggle input").Change(false);

        cut.Find("button[aria-label=Vorige]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void MediaEnd_ShouldMoveToNextItem()
    {
        Seed();
        var cut = RenderSlideshow();
        Start(cut);

        cut.Find(".media video").TriggerEvent("onended", EventArgs.Empty);

        cut.Find(".position").TextContent.Should().Be("2 / 3");
    }

    [Fact]
    public void Keyboard_ShouldNavigateAndEscapeShouldStop()
    {
        Seed();
        var cut = RenderSlideshow();
        Start(cut);

        cut.Find(".stage").KeyDown(key: "ArrowRight");
        cut.Find(".position").TextContent.Should().Be("2 / 3");

        cut.Find(".stage").KeyDown(key: "ArrowLeft");
        cut.Find(".position").TextContent.Should().Be("1 / 3");

        cut.Find(".stage").KeyDown(key: " ");
        cut.Find("button[aria-label=Afspelen]").Should().NotBeNull();

        cut.Find(".stage").KeyDown(key: "Escape");
        cut.FindAll(".stage").Should().BeEmpty();
    }

    [Fact]
    public void FavoritesOnly_ShouldShowOnlyFavoriteMedia()
    {
        var seeded = Seed();
        CookieHas(seeded.Approved, seeded.Pending);
        var cut = RenderSlideshow();

        cut.Find("#slideshowFavorites").Change(true);

        // The favorite PDF is not media, so only the photo is left
        cut.Find(".item-count").TextContent.Should().Be("1 item");
        Start(cut);
        cut.Find(".media img").GetAttribute("src").Should().Be($"/api/files/{seeded.Approved.Id}");
    }

    [Fact]
    public void FavoritesWithoutMedia_ShouldShowEmptyStateAndDisableStart()
    {
        var seeded = Seed();
        CookieHas(seeded.Pending);

        var cut = RenderSlideshow("favorites=true");

        cut.Markup.Should().Contain("Nog geen favoriete foto's, video's of geluidsopnames met deze keuzes.");
        cut.FindAll("button").First(b => b.TextContent.Contains("Diavoorstelling starten")).HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void QueryFilters_ShouldNarrowTheShowButNeverIncludeRejected()
    {
        Seed();

        RenderSlideshow($"group={Wolves.Id}").Find(".item-count").TextContent.Should().Be("2 items");
        RenderSlideshow($"group={Wolves.Id}&assignment=1").Find(".item-count").TextContent.Should().Be("1 item");
        RenderSlideshow($"status={SubmissionStatus.Rejected}").Find(".item-count").TextContent.Should().Be("0 items");
    }

    [Fact]
    public void RejectedWhileShowing_ShouldDropFromShow()
    {
        var seeded = Seed();
        var cut = RenderSlideshow();
        Start(cut);

        WithDb(db =>
        {
            var awesome = db.Submissions.Single(s => s.Id == seeded.Awesome.Id);
            awesome.Status = SubmissionStatus.Rejected;
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.Find(".position").TextContent.Should().Be("1 / 2"));
        cut.Find(".media img").GetAttribute("src").Should().Be($"/api/files/{seeded.Approved.Id}");
    }

    [Fact]
    public void SubmissionsPage_ShouldLinkToSlideshowWithCurrentFilters()
    {
        Seed();
        var cut = Render<Submissions>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(5));
        cut.Find("a.slideshow-link").GetAttribute("href").Should().Be("slideshow");

        cut.FindAll("select")[0].Change(Wolves.Id.ToString());
        cut.FindAll("select")[1].Change("2");

        cut.WaitForAssertion(() => cut.Find("a.slideshow-link").GetAttribute("href").Should().Be($"slideshow?group={Wolves.Id}&assignment=2"));
    }
}
