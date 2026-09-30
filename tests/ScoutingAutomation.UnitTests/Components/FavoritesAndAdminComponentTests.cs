using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Layout;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using ScoutingAutomation.Web.Favorites;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class FavoriteComponentTests : PageTestBase
{
    public FavoriteComponentTests()
    {
        JSInterop.Mode = JSRuntimeMode.Strict;
        JSInterop.SetupVoid("duFavorites.set", _ => true).SetVoidResult();
    }

    private void CookieHas(params Domain.Entities.Submission[] favorites) =>
        JSInterop.Setup<string>("duFavorites.get").SetResult(string.Join('.', favorites.Select(FavoritesService.KeyFor)));

    [Fact]
    public void Star_ShouldLoadToggleAndSave()
    {
        var seeded = Seed();
        CookieHas();

        var cut = Render<FavoriteStar>(p => p.Add(s => s.Submission, seeded.Approved));

        cut.WaitForAssertion(() => cut.Find("button").HasAttribute("disabled").Should().BeFalse());
        cut.Find("button").GetAttribute("aria-pressed").Should().Be("false");

        cut.Find("button").Click();

        cut.Find("button").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("button .bi-star-fill").Should().NotBeNull();
        JSInterop.Invocations.Last().Arguments[0].Should().Be(FavoritesService.KeyFor(seeded.Approved));
    }

    [Fact]
    public void Star_WhenFull_ShouldSaySo()
    {
        var seeded = Seed();
        JSInterop.Setup<string>("duFavorites.get").SetResult(string.Join('.', Enumerable.Range(0, FavoritesService.MaxFavorites).Select(i => $"k{i:D10}")));

        var cut = Render<FavoriteStar>(p => p.Add(s => s.Submission, seeded.Approved));
        cut.WaitForAssertion(() => cut.Find("button").HasAttribute("disabled").Should().BeFalse());
        cut.Find("button").Click();

        cut.Find("button").ClassList.Should().Contain("is-full");
        cut.Find("button").GetAttribute("title").Should().Contain("Favorites are full");
    }

    [Fact]
    public async Task Star_ShouldFollowOtherStarsAndStopAfterClose()
    {
        var seeded = Seed();
        CookieHas();
        var first = Render<FavoriteStar>(p => p.Add(s => s.Submission, seeded.Pending));
        var second = Render<FavoriteStar>(p => p.Add(s => s.Submission, seeded.Pending));
        first.WaitForAssertion(() => first.Find("button").HasAttribute("disabled").Should().BeFalse());

        first.Find("button").Click();

        second.WaitForAssertion(() => second.Find("button").GetAttribute("aria-pressed").Should().Be("true"));
        await DisposeComponentsAsync();
        var act = () => Services.GetRequiredService<FavoritesService>().ToggleAsync(seeded.Pending);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void SubmissionsPage_ShouldFilterOnFavorites()
    {
        var seeded = Seed();
        CookieHas(seeded.Pending);
        var cut = Render<Submissions>();
        cut.WaitForAssertion(() => cut.Find("#favoritesOnly").HasAttribute("disabled").Should().BeFalse());
        cut.Find("label[for=favoritesOnly]").TextContent.Should().Contain("(1)");

        cut.Find("#favoritesOnly").Change(true);

        cut.FindAll("tbody tr").Should().ContainSingle().Which.TextContent.Should().Contain("DBU 1.pdf");
        cut.Find("button.btn-scout-secondary").TextContent.Should().Contain("Clear filters");

        // Unstarring the last favorite empties the filtered list right away
        cut.Find("tbody tr .favorite-star").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No submissions match these filters."));

        cut.Find("button.btn-scout-secondary").Click();
        cut.FindAll("tbody tr").Should().HaveCount(5);
    }

    [Fact]
    public void DetailAndAssignmentPages_ShouldHaveStars()
    {
        var seeded = Seed();
        CookieHas(seeded.Approved);

        var detail = Render<SubmissionDetail>(p => p.Add(c => c.Id, seeded.Approved.Id));
        detail.WaitForAssertion(() => detail.Find(".favorite-star").GetAttribute("aria-pressed").Should().Be("true"));

        var assignment = Render<AssignmentDetail>(p => p.Add(c => c.Number, 1));
        assignment.WaitForAssertion(() => assignment.FindAll(".favorite-star").Should().HaveCount(3));
        assignment.FindAll(".favorite-star[aria-pressed=true]").Should().ContainSingle();
    }
}

public class AdminOnlyComponentTests : PageTestBase
{
    [Fact]
    public void Dashboard_ForJury_ShouldShowManualModeOnlyAsBadge()
    {
        LogInAsJury();
        Services.GetRequiredService<RuntimeSettingsService>().SetManualMode(true);

        var cut = Render<Home>();

        cut.FindAll("#manualMode").Should().BeEmpty();
        cut.Find("span.badge-pending").TextContent.Should().Be("Manual mode");
    }

    [Fact]
    public void Dashboard_ForJury_WithoutManualMode_ShouldShowNothing()
    {
        LogInAsJury();

        var cut = Render<Home>();

        cut.FindAll("#manualMode").Should().BeEmpty();
        cut.Markup.Should().NotContain("Manual mode");
    }

    [Fact]
    public void Configuration_ForJury_ShouldPointToAdminModeForRebuild()
    {
        LogInAsJury();

        var cut = Render<Configuration>();

        cut.Markup.Should().Contain("Only admins can rebuild");
        cut.Find("a[href=admin]").TextContent.Should().Be("Switch to admin mode");
        cut.FindAll(".rebuild-panel").Should().BeEmpty();
    }

    [Fact]
    public void SubmissionDetail_ForJury_ShouldHideOverrideAndShowReviewerNames()
    {
        Settings.ReviewerNames = new() { ["U_JURY"] = "Eva" };
        var seeded = Seed();
        LogInAsJury();

        var cut = Render<SubmissionDetail>(p => p.Add(c => c.Id, seeded.Approved.Id));

        cut.WaitForAssertion(() => cut.FindAll(".timeline-item").Should().ContainSingle());
        cut.FindAll("details.override-panel").Should().BeEmpty();
        var reviewer = cut.Find(".timeline-item span.fw-semibold");
        reviewer.TextContent.Should().Be("Eva");
        reviewer.GetAttribute("title").Should().Be("U_JURY");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Layout_ShouldShowAdminBadgeAndNavigation(bool admin)
    {
        if (!admin) LogInAsJury();

        var cut = Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "x"))));

        cut.FindAll(".top-row a[href=admin]").Any().Should().Be(admin);
        cut.FindAll("nav a[href=admin]").Should().ContainSingle();
        var logout = cut.Find("form[action=logout]");
        logout.GetAttribute("method").Should().Be("post");
        logout.QuerySelector("button.logout").Should().NotBeNull();
    }

    [Fact]
    public void RedirectToLogin_ShouldReloadTheLoginPageWithTheCurrentPage()
    {
        var navigation = Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>();
        navigation.NavigateTo("groups?x=1");

        Render<RedirectToLogin>();

        navigation.Uri.Should().EndWith("login?ReturnUrl=%2Fgroups%3Fx%3D1");
        navigation.History.First().Options.ForceLoad.Should().BeTrue();
    }

    [Fact]
    public void Simulator_ShouldSuggestReviewersWithTheirNames()
    {
        Settings.ReviewerNames = new() { ["U_JURY"] = "Eva" };
        Services.GetRequiredService<RuntimeSettingsService>().AddReviewer("U_ADDED");

        var cut = Render<SlackSimulator>();

        cut.FindAll("#configured-reviewers option").Select(o => (o.GetAttribute("value"), o.TextContent))
            .Should().Equal(("U_JURY", "Eva"), ("U_ADDED", "U_ADDED"));
    }

    [Fact]
    public void SubmissionsPage_ReviewerFilter_ShouldMatchNames()
    {
        Settings.ReviewerNames = new() { ["U_JURY"] = "Eva de Jury" };
        Seed();
        var cut = Render<Submissions>();
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(5));

        cut.FindAll("input[type=text]")[1].Input("de jury");

        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().HaveCount(2));
    }
}
