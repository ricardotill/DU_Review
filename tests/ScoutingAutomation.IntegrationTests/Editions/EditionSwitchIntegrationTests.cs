using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Editions;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Editions;

/// <summary>
/// Next year's game in the running app: prepared next to the current one, started, and from then on Slack, the pages,
/// the files and the group logins are about the new edition, while the old one stays with its final standing.
/// </summary>
public class EditionSwitchIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _slackClient = default!;

    public EditionSwitchIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack);
    }

    public Task InitializeAsync()
    {
        _slackClient = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<T> EditionsAsync<T>(Func<EditionService, Task<T>> action) =>
        _app.GetAsync(sp => action(sp.GetRequiredService<EditionService>()));

    private async Task<Guid> UploadAsync(string text)
    {
        var ts = SlackEvents.Ts();
        var upload = await _app.SendEventAsync(_slackClient, SlackEvents.Message(Channel, ts, text, SlackEvents.File("foto.jpg")));
        await _app.SendEventAsync(_slackClient, SlackEvents.Reaction(Channel, ts, "thumbsup"));
        return Guid.Parse(upload.GetProperty("createdSubmissionIds")[0].GetString()!);
    }

    [Fact]
    public async Task StartingANewEdition_ShouldMoveSlackPagesFilesAndLoginsToIt()
    {
        var old = await UploadAsync("Opdracht 5: onze houten lepel!");
        var zeearenden = (await _app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync(Channel)))!;
        await _app.EnsureAccountAsync("zeearenden@du.test", AccountKind.Group, zeearenden.TeamId);

        var (next, error) = await EditionsAsync(s => s.CreateAsync("DU volgend jaar", DateTimeOffset.UtcNow.Year + 1, copyGroups: true));
        error.Should().BeNull();
        (await EditionsAsync(s => s.ActivateAsync(next!.Id))).Should().BeNull();

        // Slack: the same channel now belongs to the group in the new edition, with its own folder
        var fresh = await UploadAsync("Opdracht 5: weer een lepel");
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetByIdAsync(fresh)))!;
        submission.GroupId.Should().NotBe(zeearenden.Id);
        submission.StoragePath.Should().StartWith(Path.Combine(_app.StorageRoot, EditionService.EditionsFolderName, "du-volgend-jaar", "de-zeearenden"));
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetByIdAsync(old))).Should().BeNull("the old edition is not shown");

        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        stats.GroupStatistics.Single(g => g.GroupName == "De Zeearenden").Score.Should().Be(2, "only this year's lepel counts");

        // The old edition kept its submission and its final standing
        var archived = await _app.GetAsync(sp => sp.GetRequiredService<ScoutingDbContext>().Editions.AsNoTracking().SingleAsync(e => e.Status == EditionStatus.Archived));
        EditionService.ReadStanding(archived).Should().Contain(s => s.GroupName == "De Zeearenden" && s.Score == 2);
        (await _app.GetAsync(sp => sp.GetRequiredService<ScoutingDbContext>().Submissions.IgnoreQueryFilters().CountAsync(s => s.Id == old))).Should().Be(1);

        // The group logs in to its group in the new edition and sees the old one under its earlier editions
        using var group = await _app.CreateClientForAsync("zeearenden@du.test");
        (await group.GetAsync($"/api/files/{fresh}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await group.GetAsync($"/api/files/{old}")).StatusCode.Should().NotBe(HttpStatusCode.OK);
        var history = await group.GetStringAsync("/edities");
        history.Should().Contain(archived.Name).And.Contain("De Zeearenden");
    }

    [Fact]
    public async Task TeamNotInTheActiveEdition_ShouldStartOnItsEarlierEditions()
    {
        var team = new Team { Name = "Vorig jaar" };
        await _app.GetAsync(async sp =>
        {
            var db = sp.GetRequiredService<ScoutingDbContext>();
            db.Teams.Add(team);
            return await db.SaveChangesAsync();
        });
        await _app.EnsureAccountAsync("vorig-jaar@du.test", AccountKind.Group, team.Id);
        using var client = _app.CreateClient();

        var login = await TestApp.LogInAsync(client, "vorig-jaar@du.test", TestApp.Password);

        login.Headers.Location!.OriginalString.Should().EndWith("/edities");
        (await client.GetAsync("/edities")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/account")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/submissions")).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
