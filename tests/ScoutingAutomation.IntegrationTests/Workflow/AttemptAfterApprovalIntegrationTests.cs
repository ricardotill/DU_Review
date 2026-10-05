using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// The review rules: new attempts after approval, and reviewers who disagree. Live mode against a fake Slack API.
/// </summary>
public class AttemptAfterApprovalIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C02BOSUILEN";
    private const string SecondReviewer = "U_JURY2";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public AttemptAfterApprovalIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack, new Dictionary<string, string?>
        {
            ["Scouting:BlockAttemptsAfterApproval"] = "true",
            ["Scouting:Reviewers:1"] = SecondReviewer
        });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts));

    private async Task<string> UploadAsync(int number, string? reaction = null)
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, $"Opdracht {number}", SlackEvents.File($"poging{number}.jpg")));
        if (reaction != null)
        {
            await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, reaction));
        }

        return ts;
    }

    [Fact]
    public async Task NewAttemptAfterApproval_ShouldBeRefusedWithALock()
    {
        var approved = await UploadAsync(40, "thumbsup");
        (await Submissions(approved)).Single().Status.Should().Be(SubmissionStatus.Approved);

        var retry = await UploadAsync(40);

        (await Submissions(retry)).Should().BeEmpty();
        _slack.Calls("reactions.add").Should().ContainSingle(c => c["timestamp"] == retry).Which["name"].Should().Be("lock");
        _slack.ThreadReplies(retry).Should().ContainSingle().Which["text"].Should().StartWith("Opdracht 40 is al goedgekeurd");
    }

    [Fact]
    public async Task EditToACompletedAssignment_ShouldNotMoveTheSubmission()
    {
        await UploadAsync(44, "heart");
        var other = await UploadAsync(45);
        var files = new[] { SlackEvents.File("poging45.jpg") };

        await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, other, "Opdracht 45", "Opdracht 44", files));

        (await Submissions(other)).Single().AssignmentCode.Should().Be("45");
        _slack.Calls("reactions.add").Should().ContainSingle(c => c["timestamp"] == other).Which["name"].Should().Be("lock");
    }

    [Fact]
    public async Task NewAttemptAfterRejection_ShouldCount()
    {
        var rejected = await UploadAsync(41, "x");
        (await Submissions(rejected)).Single().Status.Should().Be(SubmissionStatus.Rejected);

        var retry = await UploadAsync(41);

        (await Submissions(retry)).Should().ContainSingle().Which.Status.Should().Be(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task SwitchedOffInTheApp_NewAttemptAfterApprovalShouldCount()
    {
        var runtime = _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.SetBlockAttemptsAfterApproval(false);
        try
        {
            await UploadAsync(42, "thumbsup");

            var retry = await UploadAsync(42);

            (await Submissions(retry)).Should().ContainSingle();
            _slack.Writes.Should().NotContain(c => c["timestamp"] == retry);
        }
        finally
        {
            runtime.ResetBlockAttemptsAfterApproval();
        }

        runtime.BlockAttemptsAfterApproval.Should().BeTrue("the setting from appsettings applies again");
    }

    [Fact]
    public async Task ReviewersWhoDisagree_ShouldGetTheBestStatusUntilTheyTakeItBack()
    {
        var ts = await UploadAsync(43);
        var key = FakeSlackApi.Key(Channel, ts);
        _slack.Reactions[key] = new[] { SlackEvents.Reacted("heart", SecondReviewer), SlackEvents.Reacted("x", TestApp.Reviewer) };

        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "x"));

        var awesome = (await Submissions(ts)).Single();
        awesome.Status.Should().Be(SubmissionStatus.Awesome, "the best review counts, whatever order the reactions came in");
        awesome.StoragePath.Should().Contain(Path.Combine("de-bosuilen", "awesome"));

        _slack.Reactions[key] = new[] { SlackEvents.Reacted("x", TestApp.Reviewer) };
        await _app.SendEventAsync(_client, SlackEvents.ReactionRemoved(Channel, ts, "heart", SecondReviewer));

        var rejected = (await Submissions(ts)).Single();
        rejected.Status.Should().Be(SubmissionStatus.Rejected);
        rejected.StoragePath.Should().Contain(Path.Combine("de-bosuilen", "rejected"));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
