using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// Logging in with the shared access key, admin mode, and regenerating keys.
/// </summary>
public class AccessKeyIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;

    public AccessKeyIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private Task<HttpResponseMessage> LogIn(HttpClient client, string key, string page = "/login") =>
        TestApp.PostFormAsync(client, page, "login", new() { ["Input.Key"] = key });

    private static async Task<bool> IsLoggedIn(HttpClient client) => (await client.GetAsync("/")).StatusCode == HttpStatusCode.OK;

    private static async Task<bool> IsAdmin(HttpClient client) => (await client.GetStringAsync("/admin")).Contains("Nieuwe toegangssleutel maken");

    [Fact]
    public async Task WithoutLogin_PagesAndFilesRedirectButSlackWebhookStaysOpen()
    {
        using var client = _app.CreateClient();

        var page = await client.GetAsync("/groups");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.PathAndQuery.Should().Be("/login?ReturnUrl=%2Fgroups");

        (await client.GetAsync($"/api/files/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("/login")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TestApp.PostEventAsync(client, new { type = "url_verification", challenge = "open" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ShouldAcceptTheKeyInAnyCaseAndReturnToThePage()
    {
        using var client = _app.CreateClient();

        var response = await LogIn(client, " test-access-key ", "/login?ReturnUrl=%2Fgroups");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().EndWith("/groups");
        (await IsLoggedIn(client)).Should().BeTrue();
        (await IsAdmin(client)).Should().BeFalse();

        // Visiting the login page again goes straight back
        var again = await client.GetAsync("/login?ReturnUrl=%2Fassignments");
        again.Headers.Location!.OriginalString.Should().EndWith("/assignments");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Login_ShouldNeverRedirectToAnotherSite(string returnUrl)
    {
        using var client = _app.CreateClient();

        var response = await LogIn(client, TestApp.AccessKey, $"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");

        response.Headers.Location!.OriginalString.Should().Be("http://localhost/");
    }

    [Fact]
    public async Task WrongKeys_ShouldBeRefusedAndThenBlocked()
    {
        using var client = _app.CreateClient();

        for (var i = 0; i < LoginAttemptLimiter.MaxFailures; i++)
        {
            var wrong = await LogIn(client, "WRONG");
            (await wrong.Content.ReadAsStringAsync()).Should().Contain("Deze sleutel klopt niet.");
        }

        var blocked = await LogIn(client, TestApp.AccessKey);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Te veel verkeerde sleutels");
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task WrongAdminKeys_ShouldBeRefusedAndThenBlocked()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        for (var i = 0; i < LoginAttemptLimiter.MaxFailures; i++)
        {
            var wrong = await TestApp.PostFormAsync(client, "/admin", "enter-admin", new() { ["EnterInput.Key"] = "WRONG" });
            (await wrong.Content.ReadAsStringAsync()).Should().Contain("Dit is niet de beheerderssleutel.");
        }

        var blocked = await TestApp.PostFormAsync(client, "/admin", "enter-admin", new() { ["EnterInput.Key"] = TestApp.AdminKey });
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Te veel verkeerde sleutels");
        (await IsAdmin(client)).Should().BeFalse();
    }

    [Fact]
    public async Task AdminMode_ShouldBeEnteredWithTheAdminKeyAndLeftAgain()
    {
        using var client = await _app.CreateLoggedInClientAsync();
        (await client.GetStringAsync("/configuration")).Should().Contain("Alleen beheerders kunnen opnieuw opbouwen");

        var enter = await TestApp.PostFormAsync(client, "/admin", "enter-admin", new() { ["EnterInput.Key"] = TestApp.AdminKey });
        enter.Headers.Location!.OriginalString.Should().EndWith("/admin");

        var admin = await client.GetStringAsync("/admin");
        admin.Should().Contain(TestApp.AccessKey).And.NotContain(TestApp.AdminKey);
        (await client.GetStringAsync("/configuration")).Should().NotContain("Alleen beheerders kunnen opnieuw opbouwen");

        var left = await TestApp.PostFormAsync(client, "/admin", "leave-admin", new());
        (await left.Content.ReadAsStringAsync()).Should().Contain("Je hebt de beheermodus verlaten.").And.Contain("Naar beheermodus");
        (await IsAdmin(client)).Should().BeFalse();
        (await IsLoggedIn(client)).Should().BeTrue();
    }

    [Fact]
    public async Task RegeneratingAccessKey_ShouldLogOutEveryoneButTheAdmin()
    {
        using var jury = await _app.CreateLoggedInClientAsync();
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);

        var withoutConfirm = await TestApp.PostFormAsync(admin, "/admin", "regenerate-access", new());
        (await withoutConfirm.Content.ReadAsStringAsync()).Should().Contain("Vink het vakje aan om te bevestigen.");
        (await IsLoggedIn(jury)).Should().BeTrue();

        var response = await TestApp.PostFormAsync(admin, "/admin", "regenerate-access", new() { ["RegenerateAccess.Confirm"] = "true" });
        var html = await response.Content.ReadAsStringAsync();

        html.Should().Contain("Iedereen anders is uitgelogd");
        var newKey = _app.Factory.Services.GetRequiredService<AccessKeyService>().AccessKey;
        newKey.Should().NotBe(TestApp.AccessKey);
        html.Should().Contain(newKey);
        (await IsLoggedIn(jury)).Should().BeFalse();
        (await IsLoggedIn(admin)).Should().BeTrue();
        (await IsAdmin(admin)).Should().BeTrue();

        using var newcomer = _app.CreateClient();
        (await LogIn(newcomer, TestApp.AccessKey)).StatusCode.Should().Be(HttpStatusCode.OK, "the old key no longer works");
        (await LogIn(newcomer, newKey)).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task RegeneratingAdminKey_ShouldShowItOnceAndDemoteOtherAdmins()
    {
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        using var otherAdmin = await _app.CreateLoggedInClientAsync(admin: true);

        var withoutConfirm = await TestApp.PostFormAsync(admin, "/admin", "regenerate-admin", new());
        (await withoutConfirm.Content.ReadAsStringAsync()).Should().Contain("Vink het vakje aan om te bevestigen.");

        var response = await TestApp.PostFormAsync(admin, "/admin", "regenerate-admin", new() { ["RegenerateAdmin.Confirm"] = "true" });
        var newKey = Regex.Match(await response.Content.ReadAsStringAsync(), "alleen nu zichtbaar: <code[^>]*>([A-Z0-9-]+)</code>").Groups[1].Value;

        newKey.Should().MatchRegex("^[A-Z0-9]{4}(-[A-Z0-9]{4}){3}$");
        (await admin.GetStringAsync("/admin")).Should().NotContain(newKey, "the admin key is shown only once");
        (await IsAdmin(admin)).Should().BeTrue();
        (await IsAdmin(otherAdmin)).Should().BeFalse();
        (await IsLoggedIn(otherAdmin)).Should().BeTrue("only admin mode is taken away");
    }

    [Fact]
    public async Task NonAdmin_CannotRegenerateKeys()
    {
        using var jury = await _app.CreateLoggedInClientAsync();
        var keys = _app.Factory.Services.GetRequiredService<AccessKeyService>();
        var before = keys.AccessKey;

        await TestApp.PostFormAsync(jury, "/admin", "regenerate-access", new() { ["RegenerateAccess.Confirm"] = "true" });

        keys.AccessKey.Should().Be(before);
        (await IsLoggedIn(jury)).Should().BeTrue();
    }

    [Fact]
    public async Task Logout_ShouldEndTheLogin()
    {
        using var client = await _app.CreateLoggedInClientAsync();
        var home = await client.GetStringAsync("/");
        var token = Regex.Match(home, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        response.Headers.Location!.OriginalString.Should().Be("/login");
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task RegeneratedKey_ShouldSurviveARestart()
    {
        var keysFile = Path.Combine(_app.StorageRoot, "access-keys.json");
        var newKey = _app.Factory.Services.GetRequiredService<AccessKeyService>().RegenerateAccessKey();

        File.ReadAllText(keysFile).Should().Contain(newKey);
        var restarted = new AccessKeyService(
            Microsoft.Extensions.Options.Options.Create(new Contracts.Configuration.ScoutingSettings { KeysFile = keysFile, AccessKey = TestApp.AccessKey }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AccessKeyService>.Instance);
        restarted.IsAccessKey(newKey).Should().BeTrue();
        restarted.IsAccessKey(TestApp.AccessKey).Should().BeFalse("the configured key must not come back");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
