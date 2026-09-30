using Bunit;
using FluentAssertions;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Web.Favorites;
using Xunit;

namespace ScoutingAutomation.UnitTests.Web;

public class FavoritesServiceTests : BunitContext
{
    private static Submission Sub(string ts, string file = "a.jpg") => new() { SlackChannelId = "C1", SlackMessageId = ts, FileName = file };

    [Fact]
    public void KeyFor_ShouldBeShortAndStableAcrossRebuilds()
    {
        var original = Sub("1.0");
        var rebuilt = Sub("1.0");

        FavoritesService.KeyFor(original).Should().Be(FavoritesService.KeyFor(rebuilt));
        FavoritesService.KeyFor(original).Should().HaveLength(11).And.MatchRegex("^[A-Za-z0-9_-]+$");
        FavoritesService.KeyFor(Sub("1.0", "b.jpg")).Should().NotBe(FavoritesService.KeyFor(original));
    }

    [Fact]
    public async Task Load_ShouldReadTheCookieOnce()
    {
        var favorite = Sub("1.0");
        JSInterop.Setup<string>("duFavorites.get").SetResult($"{FavoritesService.KeyFor(favorite)}..other");
        var service = new FavoritesService(JSInterop.JSRuntime);
        var changes = 0;
        service.Changed += () => changes++;

        service.IsLoaded.Should().BeFalse();
        await service.EnsureLoadedAsync();
        await service.EnsureLoadedAsync();

        service.IsLoaded.Should().BeTrue();
        service.Count.Should().Be(2);
        service.IsFavorite(favorite).Should().BeTrue();
        service.IsFavorite(Sub("2.0")).Should().BeFalse();
        JSInterop.Invocations.Count(i => i.Identifier == "duFavorites.get").Should().Be(1);
        changes.Should().Be(1);
    }

    [Fact]
    public async Task EmptyCookie_ShouldGiveNoFavorites()
    {
        JSInterop.Setup<string>("duFavorites.get").SetResult(null!);
        var service = new FavoritesService(JSInterop.JSRuntime);

        await service.EnsureLoadedAsync();

        service.Count.Should().Be(0);
    }

    [Fact]
    public async Task Toggle_ShouldAddAndRemoveAndSaveTheCookie()
    {
        JSInterop.Setup<string>("duFavorites.get").SetResult("");
        JSInterop.SetupVoid("duFavorites.set", _ => true).SetVoidResult();
        var service = new FavoritesService(JSInterop.JSRuntime);
        var submission = Sub("1.0");

        (await service.ToggleAsync(submission)).Should().BeTrue();
        service.IsFavorite(submission).Should().BeTrue();
        JSInterop.Invocations.Last().Arguments[0].Should().Be(FavoritesService.KeyFor(submission));

        (await service.ToggleAsync(submission)).Should().BeTrue();
        service.IsFavorite(submission).Should().BeFalse();
        JSInterop.Invocations.Last().Arguments[0].Should().Be("");
    }

    [Fact]
    public async Task Toggle_WhenFull_ShouldRefuseNewFavoritesButAllowRemoving()
    {
        var existing = Enumerable.Range(0, FavoritesService.MaxFavorites + 10).Select(i => $"key{i:D8}").ToList();
        JSInterop.Setup<string>("duFavorites.get").SetResult(string.Join('.', existing));
        JSInterop.SetupVoid("duFavorites.set", _ => true).SetVoidResult();
        var service = new FavoritesService(JSInterop.JSRuntime);

        (await service.ToggleAsync(Sub("9.9"))).Should().BeFalse();

        service.Count.Should().Be(FavoritesService.MaxFavorites, "extra keys in the cookie are ignored");
        JSInterop.Invocations.Should().NotContain(i => i.Identifier == "duFavorites.set");
    }
}
