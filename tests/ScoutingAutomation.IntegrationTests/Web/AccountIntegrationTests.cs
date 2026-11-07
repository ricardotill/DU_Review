using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Infrastructure.Accounts;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>Logging in with an account, wrong passwords, the password links, blocking, and the first admin.</summary>
public class AccountIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingMailer _mailer = new();
    private TestApp _app;

    public AccountIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        // Links asked for without a login only use the configured address, never the request's Host header
        _app = new TestApp(factory, new Dictionary<string, string?> { ["Scouting:PublicUrl"] = "https://du.test" }, services: s =>
        {
            s.AddSingleton<IAccountMailer>(_mailer);
            // Checked at every request, so a blocked account is out at once instead of within a minute
            s.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static async Task<bool> IsLoggedIn(HttpClient client) => (await client.GetAsync("/")).StatusCode == HttpStatusCode.OK;

    [Fact]
    public async Task WithoutLogin_PagesAndFilesRedirectButSlackWebhookAndPasswordPagesStayOpen()
    {
        using var client = _app.CreateClient();

        var page = await client.GetAsync("/groups");
        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.PathAndQuery.Should().Be("/login?ReturnUrl=%2Fgroups");

        (await client.GetAsync($"/api/files/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await client.GetAsync("/login")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/wachtwoord-vergeten")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/wachtwoord-instellen")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TestApp.PostEventAsync(client, new { type = "url_verification", challenge = "open" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_ShouldAcceptTheAddressInAnyCaseAndReturnToThePage()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        var response = await TestApp.LogInAsync(client, " EVA@du.test ", TestApp.Password, "/login?ReturnUrl=%2Fgroups");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().EndWith("/groups");
        (await IsLoggedIn(client)).Should().BeTrue();
        (await client.GetStringAsync("/admin")).Should().Contain("Alleen voor beheerders");

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
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        var response = await TestApp.LogInAsync(client, "eva@du.test", TestApp.Password, $"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");

        response.Headers.Location!.OriginalString.Should().Be("http://localhost/");
    }

    [Fact]
    public async Task LoginFormSentAgain_AfterLoggingIn_ShouldGoToTheStartPageInsteadOf400()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();
        const string page = "/login?ReturnUrl=%2Fgroups";
        var html = await client.GetStringAsync(page);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        FormUrlEncodedContent Form() => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["_handler"] = "login",
            ["Input.Email"] = "eva@du.test", ["Input.Password"] = TestApp.Password,
        });

        (await client.PostAsync(page, Form())).StatusCode.Should().Be(HttpStatusCode.Redirect);
        // The same form again, now with the login cookie: its token was made for an anonymous visitor
        var again = await client.PostAsync(page, Form());

        again.StatusCode.Should().Be(HttpStatusCode.Redirect);
        again.Headers.Location!.OriginalString.Should().Be("/groups");
        (await IsLoggedIn(client)).Should().BeTrue();
    }

    [Fact]
    public async Task LoginForm_WithoutAValidToken_ShouldStillBeRefused()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["_handler"] = "login", ["Input.Email"] = "eva@du.test", ["Input.Password"] = TestApp.Password,
        }));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task WrongPassword_AndUnknownAddress_ShouldGetTheSameAnswer()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        var wrong = await (await TestApp.LogInAsync(client, "eva@du.test", "niet het wachtwoord")).Content.ReadAsStringAsync();
        var unknown = await (await TestApp.LogInAsync(client, "niemand@du.test", "niet het wachtwoord")).Content.ReadAsStringAsync();

        wrong.Should().Contain("Dit e-mailadres of wachtwoord klopt niet.");
        unknown.Should().Contain("Dit e-mailadres of wachtwoord klopt niet.");
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task WrongPasswords_ShouldLockTheAccountEvenForTheRightPassword()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            await TestApp.LogInAsync(client, "eva@du.test", "niet het wachtwoord");
        }

        var locked = await TestApp.LogInAsync(client, "eva@du.test", TestApp.Password);
        (await locked.Content.ReadAsStringAsync()).Should().Contain("Te veel verkeerde wachtwoorden voor dit account");
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task ManyFailedLogins_FromOneAddress_ShouldBeBlockedAcrossAccounts()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        for (var i = 0; i < LoginAttemptLimiter.MaxFailures; i++)
        {
            await TestApp.LogInAsync(client, $"gok{i}@du.test", "niet het wachtwoord");
        }

        var blocked = await TestApp.LogInAsync(client, "eva@du.test", TestApp.Password);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Te veel mislukte pogingen");
    }

    [Fact]
    public async Task Logout_ShouldEndTheLogin()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        var html = await client.GetStringAsync("/");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var logout = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));

        logout.Headers.Location!.OriginalString.Should().EndWith("/login");
        (await IsLoggedIn(client)).Should().BeFalse();
    }

    [Fact]
    public async Task BlockedAccount_ShouldBeLoggedOutAndNotLogInAgain()
    {
        var eva = await _app.EnsureAccountAsync("eva@du.test");
        using var client = await _app.CreateClientForAsync("eva@du.test");
        var admin = await _app.GetAsync(sp => sp.GetRequiredService<UserManager<AppUser>>().FindByEmailAsync(TestApp.AdminEmail));

        (await _app.GetAsync(sp => sp.GetRequiredService<AccountService>().SetDisabledAsync(eva.Id, admin!.Id, true))).Should().BeNull();

        (await IsLoggedIn(client)).Should().BeFalse();
        var again = await TestApp.LogInAsync(client, "eva@du.test", TestApp.Password);
        (await again.Content.ReadAsStringAsync()).Should().Contain("Dit e-mailadres of wachtwoord klopt niet.");
    }

    [Fact]
    public async Task ForgottenPassword_ShouldMailALinkThatSetsANewPasswordAndLogsIn()
    {
        var newPassword = TestApp.NewPassword();
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();

        var asked = await TestApp.PostFormAsync(client, "/wachtwoord-vergeten", "forgot-password", new() { ["Input.Email"] = "eva@du.test" });
        (await asked.Content.ReadAsStringAsync()).Should().Contain("Als er een account is met dit adres");
        var link = _mailer.LastLinkTo("eva@du.test");

        var set = await TestApp.PostFormAsync(client, link, "set-password", new()
        {
            ["Input.Id"] = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://x" + link).Query)["id"]!,
            ["Input.Code"] = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri("http://x" + link).Query)["code"]!,
            ["Input.Password"] = newPassword,
            ["Input.Repeat"] = newPassword
        });

        set.StatusCode.Should().Be(HttpStatusCode.Redirect, await set.Content.ReadAsStringAsync());
        (await IsLoggedIn(client)).Should().BeTrue();
        using var other = _app.CreateClient();
        (await TestApp.LogInAsync(other, "eva@du.test", TestApp.Password)).StatusCode.Should().Be(HttpStatusCode.OK, "the old password no longer works");
        (await TestApp.LogInAsync(other, "eva@du.test", newPassword)).StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task ForgottenPassword_WithAForgedHost_ShouldStillLinkToThePublicAddress()
    {
        await _app.EnsureAccountAsync("eva@du.test");
        using var client = _app.CreateClient();
        client.DefaultRequestHeaders.Host = "evil.example";

        await TestApp.PostFormAsync(client, "/wachtwoord-vergeten", "forgot-password", new() { ["Input.Email"] = "eva@du.test" });

        var body = _mailer.Sent.Last(m => m.To == "eva@du.test").Body;
        body.Should().Contain("https://du.test/wachtwoord-instellen?").And.NotContain("evil.example");
    }

    [Fact]
    public async Task ForgottenPassword_ForAnUnknownAddress_ShouldLookTheSameAndSendNothing()
    {
        using var client = _app.CreateClient();

        var asked = await TestApp.PostFormAsync(client, "/wachtwoord-vergeten", "forgot-password", new() { ["Input.Email"] = "niemand@du.test" });

        (await asked.Content.ReadAsStringAsync()).Should().Contain("Als er een account is met dit adres");
        _mailer.Sent.Should().NotContain(m => m.To == "niemand@du.test");
    }

    [Fact]
    public async Task ChangedPassword_ShouldEndOtherLoginsButKeepThisOne()
    {
        var newPassword = TestApp.NewPassword();
        await _app.EnsureAccountAsync("eva@du.test");
        using var phone = await _app.CreateClientForAsync("eva@du.test");
        using var laptop = await _app.CreateClientForAsync("eva@du.test");

        var changed = await TestApp.PostFormAsync(laptop, "/account", "change-password", new()
        {
            ["Input.Current"] = TestApp.Password,
            ["Input.Password"] = newPassword,
            ["Input.Repeat"] = newPassword
        });

        (await changed.Content.ReadAsStringAsync()).Should().Contain("Je wachtwoord is gewijzigd");
        (await IsLoggedIn(laptop)).Should().BeTrue();
        (await IsLoggedIn(phone)).Should().BeFalse();
    }

    [Fact]
    public async Task Admin_ShouldSeeTheAccountsAndJuryShouldNot()
    {
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        using var jury = await _app.CreateLoggedInClientAsync();

        (await admin.GetStringAsync("/admin")).Should().Contain("Accounts").And.Contain("Er is geen mailserver ingesteld")
            .And.Contain("Edities").And.Contain($"DU {DateTimeOffset.UtcNow.Year}").And.Contain("De Zeearenden");
        (await jury.GetStringAsync("/admin")).Should().Contain("Alleen voor beheerders").And.NotContain(TestApp.AdminEmail);
    }

    [Fact]
    public async Task DatabaseFromBeforeAccounts_ShouldGetTheAccountTablesAndTheFirstAdmin()
    {
        // As if the database was made by a version without accounts: the account tables are not there
        await _app.GetAsync(async sp =>
        {
            var db = sp.GetRequiredService<AccountsDbContext>().Database;
            foreach (var table in new[] { "AspNetUserTokens", "AspNetUserRoles", "AspNetUserLogins", "AspNetUserClaims", "AspNetRoleClaims", "AspNetUsers", "AspNetRoles" })
            {
                await db.ExecuteSqlRawAsync($"DROP TABLE \"{table}\"");
            }

            return 0;
        });

        _app = await _app.RestartAsync();

        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        (await IsLoggedIn(admin)).Should().BeTrue();
        (await _app.GetAsync(sp => sp.GetRequiredService<Domain.Interfaces.ISubmissionRepository>()
            .SearchSubmissionsAsync(new Domain.Models.SubmissionFilter()))).Should().NotBeEmpty("the game data stays");
    }

    [Fact]
    public async Task WithoutAnyAdmin_AndNoBootstrapAddress_ButAMailServer_TheAppShouldNotStart()
    {
        await using var app = new TestApp(_factory, new Dictionary<string, string?>
        {
            ["Scouting:Accounts:BootstrapAdminEmail"] = "",
            ["Scouting:Email:Host"] = "smtp.du.test",
            ["Scouting:Email:FromAddress"] = "noreply@du.test",
        });

        var act = () => app.CreateClient();

        act.Should().Throw<Exception>().Which.ToString().Should().Contain("BOOTSTRAP_ADMIN_EMAIL");
    }

    [Fact]
    public async Task WithoutAnyAdmin_BootstrapAddressOrMailServer_AdminAdminShouldLogIn()
    {
        await using var app = new TestApp(_factory, new Dictionary<string, string?>
        {
            ["Scouting:Accounts:BootstrapAdminEmail"] = "",
            ["Scouting:Accounts:BootstrapAdminPassword"] = "",
        });
        using var client = app.CreateClient();

        var response = await TestApp.LogInAsync(client, "admin", "admin");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await IsLoggedIn(client)).Should().BeTrue();
        (await client.GetAsync("/admin")).StatusCode.Should().Be(HttpStatusCode.OK, "the account is an admin");
    }
}
