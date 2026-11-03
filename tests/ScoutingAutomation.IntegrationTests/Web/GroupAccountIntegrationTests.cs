using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Infrastructure.Accounts;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// Group accounts see their own dashboard, uploads and files, and nothing of the jury or other groups. The beamer account
/// sees the scoreboard and the slideshow only.
/// </summary>
public class GroupAccountIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private Group _own = default!;
    private Submission _ownSubmission = default!;
    private Submission _otherSubmission = default!;

    public GroupAccountIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    public async Task InitializeAsync()
    {
        // The sample data: two groups that each have an upload
        var submissions = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()));
        var byGroup = submissions.GroupBy(s => s.GroupId).Take(2).ToList();
        byGroup.Should().HaveCount(2, "the sample data has uploads of several groups");
        _ownSubmission = byGroup[0].First();
        _otherSubmission = byGroup[1].First();
        _own = (await _app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetByIdAsync(_ownSubmission.GroupId)))!;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private async Task<HttpClient> CreateGroupClientAsync()
    {
        await _app.EnsureAccountAsync("groep@du.test", AccountKind.Group, _own.Id);
        var client = _app.CreateClient();
        var response = await TestApp.LogInAsync(client, "groep@du.test", TestApp.Password, "/login?ReturnUrl=%2Fqueue");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().EndWith("/", "a group starts on its dashboard, not on a jury page");
        return client;
    }

    private async Task<HttpClient> CreateBeamerClientAsync()
    {
        await _app.EnsureAccountAsync("beamer@du.test", AccountKind.Beamer);
        var client = _app.CreateClient();
        var response = await TestApp.LogInAsync(client, "beamer@du.test", TestApp.Password, "/login?ReturnUrl=%2Fqueue");
        response.Headers.Location!.OriginalString.Should().EndWith("/scoreboard", "the beamer starts on the scoreboard");
        return client;
    }

    [Fact]
    public async Task GroupLogin_ShouldNotClearEarlierFailedLogins()
    {
        // Otherwise a group could guess, log in with its own account to clear the count, and guess on without limit
        using var guesser = _app.CreateClient();
        for (var i = 0; i < LoginAttemptLimiter.MaxFailures - 1; i++)
        {
            await TestApp.LogInAsync(guesser, $"gok{i}@du.test", "niet het wachtwoord");
        }

        using var group = await CreateGroupClientAsync();

        await TestApp.LogInAsync(guesser, "gok@du.test", "niet het wachtwoord");
        var blocked = await TestApp.LogInAsync(guesser, "groep@du.test", TestApp.Password);
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Te veel mislukte pogingen");
    }

    [Fact]
    public async Task Group_ShouldOpenItsOwnPages()
    {
        using var client = await CreateGroupClientAsync();

        var home = await client.GetAsync("/");
        home.StatusCode.Should().Be(HttpStatusCode.OK, home.Headers.Location?.ToString());
        (await client.GetAsync("/submissions")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/submissions/{_ownSubmission.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/geen-toegang")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/account")).StatusCode.Should().Be(HttpStatusCode.OK);

        var file = await client.GetAsync($"/api/files/{_ownSubmission.Id}");
        file.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Group_ShouldNotGetFilesOfOtherGroups()
    {
        using var client = await CreateGroupClientAsync();

        (await client.GetAsync($"/api/files/{_otherSubmission.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/queue")]
    [InlineData("/groups")]
    [InlineData("/assignments")]
    [InlineData("/assignments/1")]
    [InlineData("/messages")]
    [InlineData("/scoreboard")]
    [InlineData("/slideshow")]
    [InlineData("/standing")]
    [InlineData("/events")]
    [InlineData("/configuration")]
    [InlineData("/admin")]
    [InlineData("/api/export/scores.csv")]
    [InlineData("/api/export/files.zip")]
    public async Task Group_ShouldBeSentAwayFromJuryPages(string page)
    {
        using var client = await CreateGroupClientAsync();

        var response = await client.GetAsync(page);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/geen-toegang");
    }

    [Fact]
    public async Task Group_ShouldNotOpenTheGroupPageOfAnyGroup()
    {
        using var client = await CreateGroupClientAsync();

        var own = await client.GetAsync($"/groups/{_own.Id}");
        var other = await client.GetAsync($"/groups/{_otherSubmission.GroupId}");

        own.Headers.Location!.AbsolutePath.Should().Be("/geen-toegang");
        other.Headers.Location!.AbsolutePath.Should().Be("/geen-toegang");
    }

    [Fact]
    public async Task Group_ShouldGetTheLiveConnection()
    {
        using var client = await CreateGroupClientAsync();

        var negotiate = await client.PostAsync("/_blazor/negotiate?negotiateVersion=1", null);

        negotiate.StatusCode.Should().Be(HttpStatusCode.OK, "the dashboard and uploads are interactive pages");
    }

    [Fact]
    public async Task Dashboard_ShouldShowTheGroupAndNoJuryMenu()
    {
        using var client = await CreateGroupClientAsync();

        var html = await client.GetStringAsync("/");

        html.Should().Contain(System.Net.WebUtility.HtmlEncode(_own.Name));
        html.Should().NotContain("href=\"queue\"").And.NotContain("href=\"admin\"").And.NotContain("href=\"configuration\"");
        html.Should().Contain("Onze inzendingen");
    }

    [Fact]
    public async Task GroupAccountMovedToAnotherGroup_ShouldBeLoggedOut()
    {
        using var client = await CreateGroupClientAsync();
        var account = await _app.EnsureAccountAsync("groep@du.test", AccountKind.Group, _own.Id);
        var admin = await _app.EnsureAccountAsync(TestApp.AdminEmail);

        (await _app.GetAsync(sp => sp.GetRequiredService<AccountService>()
            .UpdateAsync(account.Id, admin.Id, "Andere groep", AccountKind.Group, _otherSubmission.GroupId))).Should().BeNull();

        // The cookie is checked against the account at most a minute later; the security stamp changed, so the login ends
        var stale = await _app.GetAsync(async sp =>
        {
            var users = sp.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
            return (await users.FindByIdAsync(account.Id.ToString()))!.SecurityStamp != account.SecurityStamp;
        });
        stale.Should().BeTrue();
    }

    [Fact]
    public async Task Beamer_ShouldOnlyOpenTheScoreboardAndSlideshow()
    {
        using var client = await CreateBeamerClientAsync();

        (await client.GetAsync("/scoreboard")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/slideshow")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var page in new[] { "/", "/queue", "/submissions", "/standing", "/configuration", "/admin", "/api/export/scores.csv" })
        {
            var response = await client.GetAsync(page);
            response.Headers.Location?.AbsolutePath.Should().Be("/geen-toegang", page);
        }
    }

    [Fact]
    public async Task Beamer_ShouldGetFilesButNoRejectedOnes()
    {
        var rejected = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .FirstOrDefault(s => s.Status == SubmissionStatus.Rejected);
        using var client = await CreateBeamerClientAsync();

        (await client.GetAsync($"/api/files/{_ownSubmission.Id}")).StatusCode.Should().Be(
            _ownSubmission.Status == SubmissionStatus.Rejected ? HttpStatusCode.NotFound : HttpStatusCode.OK);
        if (rejected != null)
        {
            (await client.GetAsync($"/api/files/{rejected.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task Jury_ShouldStillOpenEverything()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        foreach (var page in new[] { "/", "/queue", "/groups", "/standing", "/scoreboard", "/configuration", "/admin", "/account" })
        {
            (await client.GetAsync(page)).StatusCode.Should().Be(HttpStatusCode.OK, page);
        }

        (await client.GetAsync($"/api/files/{_otherSubmission.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
