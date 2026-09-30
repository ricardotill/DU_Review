using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Auth;
using Xunit;

namespace ScoutingAutomation.UnitTests.Web;

public class KeyAuthTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AccessKeyService _keys;

    public KeyAuthTests()
    {
        _keys = new AccessKeyService(Options.Create(new ScoutingSettings { KeysFile = Path.Combine(_temp.Path, "keys.json") }), NullLogger<AccessKeyService>.Instance);
    }

    [Fact]
    public void CreatePrincipal_ShouldMarkAdmins()
    {
        var jury = KeyAuth.CreatePrincipal(_keys, admin: false);
        var admin = KeyAuth.CreatePrincipal(_keys, admin: true);

        jury.Identity!.IsAuthenticated.Should().BeTrue();
        jury.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
        admin.IsInRole(KeyAuth.AdminRole).Should().BeTrue();
        admin.Identity!.Name.Should().Be("Admin");
    }

    [Fact]
    public void Revalidate_ShouldFollowKeyChanges()
    {
        var jury = KeyAuth.CreatePrincipal(_keys, admin: false);
        var admin = KeyAuth.CreatePrincipal(_keys, admin: true);

        KeyAuth.Revalidate(jury, _keys).Should().BeSameAs(jury);
        KeyAuth.Revalidate(admin, _keys).Should().BeSameAs(admin);
        KeyAuth.Revalidate(new ClaimsPrincipal(new ClaimsIdentity()), _keys).Should().BeNull();

        _keys.RegenerateAdminKey();
        var demoted = KeyAuth.Revalidate(admin, _keys);
        demoted!.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
        demoted.Identity!.IsAuthenticated.Should().BeTrue();
        KeyAuth.Revalidate(jury, _keys).Should().BeSameAs(jury);

        _keys.RegenerateAccessKey();
        KeyAuth.Revalidate(jury, _keys).Should().BeNull();
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/groups", "/groups")]
    [InlineData("/submissions?group=1", "/submissions?group=1")]
    [InlineData("groups", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    public void SafeReturnUrl_ShouldOnlyAllowLocalPaths(string? returnUrl, string expected) =>
        KeyAuth.SafeReturnUrl(returnUrl).Should().Be(expected);

    private (DefaultHttpContext Http, Mock<IAuthenticationService> Auth, KeyAuthenticationStateProvider State) CreateHttp()
    {
        var auth = new Mock<IAuthenticationService>();
        var state = new KeyAuthenticationStateProvider(_keys);
        var services = new ServiceCollection()
            .AddSingleton(_keys)
            .AddSingleton(auth.Object)
            .AddSingleton<AuthenticationStateProvider>(state)
            .BuildServiceProvider();
        return (new DefaultHttpContext { RequestServices = services }, auth, state);
    }

    private static CookieValidatePrincipalContext Validation(HttpContext http, ClaimsPrincipal principal) =>
        new(http, new AuthenticationScheme(KeyAuth.Scheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(), new AuthenticationTicket(principal, KeyAuth.Scheme));

    [Fact]
    public async Task ValidatePrincipal_ValidLogin_ShouldBeKept()
    {
        var (http, auth, _) = CreateHttp();
        var principal = KeyAuth.CreatePrincipal(_keys, admin: true);
        var context = Validation(http, principal);

        await KeyAuth.ValidatePrincipalAsync(context);

        context.Principal.Should().BeSameAs(principal);
        context.ShouldRenew.Should().BeFalse();
        auth.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidatePrincipal_AfterAdminKeyChange_ShouldDropAdminRights()
    {
        var (http, _, _) = CreateHttp();
        var context = Validation(http, KeyAuth.CreatePrincipal(_keys, admin: true));
        _keys.RegenerateAdminKey();

        await KeyAuth.ValidatePrincipalAsync(context);

        context.Principal!.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
        context.ShouldRenew.Should().BeTrue();
    }

    [Fact]
    public async Task ValidatePrincipal_AfterAccessKeyChange_ShouldLogOut()
    {
        var (http, auth, _) = CreateHttp();
        var context = Validation(http, KeyAuth.CreatePrincipal(_keys, admin: false));
        _keys.RegenerateAccessKey();

        await KeyAuth.ValidatePrincipalAsync(context);

        context.Principal.Should().BeNull();
        auth.Verify(a => a.SignOutAsync(http, KeyAuth.Scheme, It.IsAny<AuthenticationProperties>()), Times.Once);
    }

    [Fact]
    public async Task SignIn_ShouldSetTheCookieAndTheCurrentLogin()
    {
        var (http, auth, state) = CreateHttp();
        state.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));

        await KeyAuth.SignInAsync(http, _keys, admin: true);

        auth.Verify(a => a.SignInAsync(http, KeyAuth.Scheme, It.Is<ClaimsPrincipal>(p => p.IsInRole(KeyAuth.AdminRole)),
            It.Is<AuthenticationProperties>(p => p.IsPersistent)), Times.Once);
        http.User.IsInRole(KeyAuth.AdminRole).Should().BeTrue();
        (await state.GetAuthenticationStateAsync()).User.IsInRole(KeyAuth.AdminRole).Should().BeTrue();
    }

    [Fact]
    public async Task SignIn_WithoutBlazorLoginState_ShouldStillSignIn()
    {
        var auth = new Mock<IAuthenticationService>();
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().AddSingleton(auth.Object).BuildServiceProvider() };

        await KeyAuth.SignInAsync(http, _keys, admin: false);

        http.User.Identity!.IsAuthenticated.Should().BeTrue();
    }

    public void Dispose() => _temp.Dispose();
}

public class KeyAuthenticationStateProviderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly AccessKeyService _keys;

    public KeyAuthenticationStateProviderTests()
    {
        _keys = new AccessKeyService(Options.Create(new ScoutingSettings { KeysFile = Path.Combine(_temp.Path, "keys.json") }), NullLogger<AccessKeyService>.Instance);
    }

    private static async Task<ClaimsPrincipal> NextStateAsync(AuthenticationStateProvider provider, Action change)
    {
        var next = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async task => next.TrySetResult(await task);
        change();
        return (await next.Task.WaitAsync(TimeSpan.FromSeconds(5))).User;
    }

    [Fact]
    public async Task OpenTab_ShouldLoseAdminRightsThenLoginWhenKeysChange()
    {
        using var provider = new KeyAuthenticationStateProvider(_keys);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(KeyAuth.CreatePrincipal(_keys, admin: true))));

        var demoted = await NextStateAsync(provider, () => _keys.RegenerateAdminKey());
        demoted.IsInRole(KeyAuth.AdminRole).Should().BeFalse();
        demoted.Identity!.IsAuthenticated.Should().BeTrue();

        var loggedOut = await NextStateAsync(provider, () => _keys.RegenerateAccessKey());
        loggedOut.Identity!.IsAuthenticated.Should().BeFalse();
    }

    [Fact]
    public async Task StillValidLogin_ShouldNotChange()
    {
        using var provider = new KeyAuthenticationStateProvider(_keys);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(KeyAuth.CreatePrincipal(_keys, admin: false))));
        var changed = false;
        provider.AuthenticationStateChanged += _ => changed = true;

        _keys.RegenerateAdminKey();
        await provider.RevalidateAsync();

        changed.Should().BeFalse("a jury login does not depend on the admin key");
    }

    [Fact]
    public async Task WithoutLoginState_ShouldIgnoreKeyChanges()
    {
        using var provider = new KeyAuthenticationStateProvider(_keys);

        _keys.RegenerateAccessKey();
        var act = () => provider.RevalidateAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Disposed_ShouldStopListening()
    {
        var provider = new KeyAuthenticationStateProvider(_keys);
        provider.SetAuthenticationState(Task.FromResult(new AuthenticationState(KeyAuth.CreatePrincipal(_keys, admin: false))));
        var changed = false;
        provider.AuthenticationStateChanged += _ => changed = true;

        provider.Dispose();
        _keys.RegenerateAccessKey();

        changed.Should().BeFalse();
    }

    public void Dispose() => _temp.Dispose();
}
