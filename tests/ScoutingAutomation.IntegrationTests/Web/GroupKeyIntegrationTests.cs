using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Access;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// Logging in with a group key: the group's own dashboard, uploads and files, and nothing of the jury or other groups.
/// </summary>
public class GroupKeyIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private Group _own = default!;
    private Submission _ownSubmission = default!;
    private Submission _otherSubmission = default!;

    public GroupKeyIntegrationTests(WebApplicationFactory<Program> factory)
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

    public Task DisposeAsync() => Task.CompletedTask;

    private AccessKeyService Keys => _app.Factory.Services.GetRequiredService<AccessKeyService>();

    private async Task<HttpClient> CreateGroupClientAsync()
    {
        var key = Keys.GroupKey(_own.SlackChannelId) ?? Keys.RegenerateGroupKey(_own.SlackChannelId);
        var client = _app.CreateClient();
        var response = await TestApp.PostFormAsync(client, "/login?ReturnUrl=%2Fqueue", "login", new() { ["Input.Key"] = key });
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().EndWith("/", "a group starts on its dashboard, not on a jury page");
        return client;
    }

    [Fact]
    public async Task GroupLogin_ShouldNotClearEarlierWrongKeys()
    {
        // Otherwise a group could guess, log in with its own key to clear the count, and guess on without limit
        using var guesser = _app.CreateClient();
        for (var i = 0; i < LoginAttemptLimiter.MaxFailures - 1; i++)
        {
            var wrong = await TestApp.PostFormAsync(guesser, "/login", "login", new() { ["Input.Key"] = "WRONG" });
            (await wrong.Content.ReadAsStringAsync()).Should().Contain("Deze sleutel klopt niet.");
        }

        using var group = await CreateGroupClientAsync();

        var last = await TestApp.PostFormAsync(guesser, "/login", "login", new() { ["Input.Key"] = "WRONG" });
        (await last.Content.ReadAsStringAsync()).Should().Contain("Deze sleutel klopt niet.");
        var blocked = await TestApp.PostFormAsync(guesser, "/login", "login", new() { ["Input.Key"] = TestApp.AccessKey });
        (await blocked.Content.ReadAsStringAsync()).Should().Contain("Te veel verkeerde sleutels");
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
    public async Task NewGroupKey_ShouldLogTheGroupOut()
    {
        using var client = await CreateGroupClientAsync();

        Keys.RegenerateGroupKey(_own.SlackChannelId);

        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
    }

    [Fact]
    public async Task UnknownKey_ShouldNotLogIn()
    {
        using var client = _app.CreateClient();

        var response = await TestApp.PostFormAsync(client, "/login", "login", new() { ["Input.Key"] = AccessKeyService.Generate() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Deze sleutel klopt niet.");
    }

    [Fact]
    public async Task Jury_ShouldStillOpenEverything()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        foreach (var page in new[] { "/", "/queue", "/groups", "/standing", "/scoreboard", "/configuration", "/admin" })
        {
            (await client.GetAsync(page)).StatusCode.Should().Be(HttpStatusCode.OK, page);
        }

        (await client.GetAsync($"/api/files/{_otherSubmission.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
