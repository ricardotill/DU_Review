using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// A submission's whole life in the running app: upload, review, override and deletion, seen on the pages and the scoreboard.
/// </summary>
public class WorkflowEndToEndIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _slackClient = default!;
    private HttpClient _browser = default!;

    public WorkflowEndToEndIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack);
    }

    public async Task InitializeAsync()
    {
        _slackClient = _app.CreateClient();
        _browser = await _app.CreateLoggedInClientAsync();
    }

    private async Task<GroupStatistic> ZeearendenAsync() =>
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync()))
        .GroupStatistics.Single(g => g.GroupName == "De Zeearenden");

    [Fact]
    public async Task Submission_FromUploadThroughReviewOverrideAndDeletion()
    {
        // 1. A scout uploads in Slack
        var ts = SlackEvents.Ts();
        var upload = await _app.SendEventAsync(_slackClient, SlackEvents.Message(Channel, ts, "Opdracht 5: onze houten lepel!", SlackEvents.File("lepel.jpg")));
        var id = Guid.Parse(upload.GetProperty("createdSubmissionIds")[0].GetString()!);
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetByIdAsync(id)))!;
        var storedName = Path.GetFileName(submission.StoragePath);

        submission.Status.Should().Be(SubmissionStatus.Pending);
        Path.GetDirectoryName(submission.StoragePath).Should().Be(Path.Combine(_app.StorageRoot, "de-zeearenden"));
        (await _browser.GetStringAsync("/queue")).Should().Contain($"submissions/{id}", "it waits for the jury");
        (await _browser.GetStringAsync("/submissions")).Should().Contain(storedName);
        (await _browser.GetByteArrayAsync($"/api/files/{id}")).Should().Equal(FakeSlackApi.FileContent);
        (await ZeearendenAsync()).Score.Should().Be(0);

        // 2. The jury approves it with an emoji
        await _app.SendEventAsync(_slackClient, SlackEvents.Reaction(Channel, ts, "thumbsup"));

        var approved = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetByIdAsync(id)))!;
        approved.Status.Should().Be(SubmissionStatus.Approved);
        approved.StoragePath.Should().Contain(Path.Combine("de-zeearenden", "approved"));
        (await ZeearendenAsync()).Score.Should().Be(2, "assignment 5 is worth 2 points");
        (await _browser.GetStringAsync($"/groups/{approved.GroupId}")).Should().Contain($"submissions/{id}");

        // 3. Slack has problems, so the jury makes it awesome on the dashboard
        await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionWorkflowService>().OverrideStatusAsync(id, SubmissionStatus.Awesome, "Slack lag eruit"));

        (await ZeearendenAsync()).Score.Should().Be(4);
        (await _browser.GetStringAsync("/assignments/5")).Should().Contain($"submissions/{id}");

        // 4. The scout deletes the message: it no longer counts
        await _app.SendEventAsync(_slackClient, SlackEvents.Deleted(Channel, ts, "Opdracht 5: onze houten lepel!"));

        (await ZeearendenAsync()).Score.Should().Be(0);
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).Should().BeEmpty();
        (await _browser.GetStringAsync("/submissions")).Should().NotContain(storedName);
        File.Exists(Path.Combine(_app.StorageRoot, LocalFileStorageService.DeletedFolderName, "de-zeearenden", storedName)).Should().BeTrue();
        (await _browser.GetAsync($"/api/files/{id}")).StatusCode.Should().Be(System.Net.HttpStatusCode.NotFound);
    }

    public async Task DisposeAsync()
    {
        _slackClient.Dispose();
        _browser.Dispose();
        await _app.DisposeAsync();
    }
}
