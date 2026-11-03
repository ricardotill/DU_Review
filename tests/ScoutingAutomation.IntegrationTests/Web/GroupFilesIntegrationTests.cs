using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Accounts;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// Groups do not download their files from the app, to save the location's bandwidth: they are sent to the message
/// in Slack, which has the file already. The jury still gets the file itself.
/// </summary>
public class GroupFilesIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Toeristen = "C01ZEEARENDEN";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new() { Workspace = ("T0DU", "https://du-scouting.slack.com/") };

    public GroupFilesIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private static async Task<HttpClient> LogInAsGroupAsync(TestApp app)
    {
        var group = await app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Toeristen));
        await app.EnsureAccountAsync("zeearenden@du.test", AccountKind.Group, group!.Id);
        return await app.CreateClientForAsync("zeearenden@du.test");
    }

    [Fact]
    public async Task File_ForTheGroup_ShouldRedirectToSlackAndForTheJuryBeTheFile()
    {
        await using var app = TestApp.Live(_factory, _slack);
        using var events = app.CreateClient();
        var ts = SlackEvents.Ts();
        await app.SendEventAsync(events, SlackEvents.Message(Toeristen, ts, "Opdracht 3", SlackEvents.File("vuur.jpg")));
        var submission = (await app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Toeristen, ts))).Single();

        using var group = await LogInAsGroupAsync(app);
        var redirect = await group.GetAsync($"/api/files/{submission.Id}");

        redirect.StatusCode.Should().Be(HttpStatusCode.Redirect);
        redirect.Headers.Location!.ToString().Should().Be($"https://du-scouting.slack.com/archives/{Toeristen}/p{ts.Replace(".", "")}");
        redirect.Content.Headers.ContentLength.GetValueOrDefault().Should().BeLessThan(100, "no file is sent");

        using var jury = await app.CreateLoggedInClientAsync();
        var file = await jury.GetAsync($"/api/files/{submission.Id}");
        file.StatusCode.Should().Be(HttpStatusCode.OK);
        file.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task DetailPage_ForTheGroup_ShouldLinkToSlackWithoutLoadingTheFile()
    {
        await using var app = TestApp.Live(_factory, _slack);
        using var events = app.CreateClient();
        var ts = SlackEvents.Ts();
        await app.SendEventAsync(events, SlackEvents.Message(Toeristen, ts, "Opdracht 3", SlackEvents.File("vuur.jpg")));
        var submission = (await app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Toeristen, ts))).Single();

        using var group = await LogInAsGroupAsync(app);
        var html = await group.GetStringAsync($"/submissions/{submission.Id}");

        html.Should().Contain("Bekijk in Slack").And.Contain($"slack://channel?team=T0DU&amp;id={Toeristen}");
        html.Should().NotContain($"/api/files/{submission.Id}", "the page must not load the file");
    }
}
