using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// Changing a status from the dashboard when Slack has problems, in the running app in simulation mode.
/// </summary>
public class DashboardOverrideIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C03WOLVEN";
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public DashboardOverrideIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory, new Dictionary<string, string?> { ["Scouting:Reviewers:0"] = TestApp.Reviewer });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private async Task<Submission> UploadAsync(int number)
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, $"Opdracht {number}", SlackEvents.File("handmatig.jpg")));
        return (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts))).Single();
    }

    private Task<Submission> OverrideAsync(Guid id, SubmissionStatus status, string reason) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionWorkflowService>().OverrideStatusAsync(id, status, reason));

    private Task<Submission?> ReloadAsync(Guid id) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetByIdAsync(id));

    private async Task<int> WolvenScoreAsync()
    {
        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        return stats.GroupStatistics.Single(g => g.GroupName == "De Wolven").Score;
    }

    [Fact]
    public async Task Override_ShouldMoveTheFileRecordTheReasonAndScore()
    {
        var submission = await UploadAsync(82);
        var scoreBefore = await WolvenScoreAsync();

        await OverrideAsync(submission.Id, SubmissionStatus.Awesome, "  Slack lag eruit  ");

        var overridden = (await ReloadAsync(submission.Id))!;
        overridden.Status.Should().Be(SubmissionStatus.Awesome);
        overridden.StoragePath.Should().Contain(Path.Combine("de-wolven", "awesome"));
        File.Exists(overridden.StoragePath).Should().BeTrue();
        File.Exists(submission.StoragePath).Should().BeFalse();
        var review = overridden.Reviews.First();
        review.Reviewer.Should().Be(SubmissionWorkflowService.DashboardOverrideReviewer);
        review.Notes.Should().Be("Slack lag eruit");
        (await WolvenScoreAsync()).Should().Be(scoreBefore + 4, "assignment 82 is worth 4 points when awesome");

        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync($"/submissions/{submission.Id}")).Should().Contain("Handmatig aangepast").And.Contain("Slack lag eruit");
    }

    [Fact]
    public async Task OverrideBackToPending_ShouldTakeAwayThePoints()
    {
        var submission = await UploadAsync(80);
        var scoreBefore = await WolvenScoreAsync();
        await OverrideAsync(submission.Id, SubmissionStatus.Approved, "goedgekeurd op papier");
        (await WolvenScoreAsync()).Should().Be(scoreBefore + 1);

        await OverrideAsync(submission.Id, SubmissionStatus.Pending, "toch nog niet");

        var pending = (await ReloadAsync(submission.Id))!;
        Path.GetDirectoryName(pending.StoragePath).Should().Be(Path.Combine(_app.StorageRoot, "de-wolven"));
        (await WolvenScoreAsync()).Should().Be(scoreBefore);
    }

    [Fact]
    public async Task OverrideWithoutReason_ShouldBeRefused()
    {
        var submission = await UploadAsync(81);

        var act = () => OverrideAsync(submission.Id, SubmissionStatus.Approved, " ");

        await act.Should().ThrowAsync<ArgumentException>();
        (await ReloadAsync(submission.Id))!.Status.Should().Be(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task ReviewInSlackAfterAnOverride_ShouldWin()
    {
        var submission = await UploadAsync(90);
        await OverrideAsync(submission.Id, SubmissionStatus.Awesome, "Slack lag eruit");

        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, submission.SlackMessageId, "x"));

        var reviewed = (await ReloadAsync(submission.Id))!;
        reviewed.Status.Should().Be(SubmissionStatus.Rejected, "Slack is leading once it works again");
        reviewed.Reviews.First().Reviewer.Should().Be(TestApp.Reviewer);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
