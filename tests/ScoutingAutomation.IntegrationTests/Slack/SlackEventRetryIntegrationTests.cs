using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Failed Slack events are kept and can be run again from the events page, without a rebuild.
/// </summary>
public class SlackEventRetryIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";

    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _client = null!;

    public SlackEventRetryIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _slack.MissingFiles.TryAdd("kapot.jpg", 0);
        _app = TestApp.Live(factory, _slack);
    }

    public async Task InitializeAsync()
    {
        _client = _app.CreateClient();
        await _app.Factory.Services.GetRequiredService<ISlackCatchUpService>().Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private T Get<T>() where T : notnull => _app.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<T>();

    private Task<IReadOnlyList<FailedSlackEvent>> PendingAsync() => Get<IFailedSlackEventRepository>().GetPendingAsync();

    private Task<IReadOnlyList<Submission>> SubmissionsAsync(string ts) => Get<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts);

    [Fact]
    public async Task FailedDownload_ShouldBeKept_AndStoredWhenRunAgain()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 3", SlackEvents.File("kapot.jpg")));

        var failure = (await PendingAsync()).Should().ContainSingle().Subject;
        failure.Kind.Should().Be(nameof(SlackEventKind.Message));
        failure.ChannelId.Should().Be(Channel);
        failure.MessageTs.Should().Be(ts);
        (await SubmissionsAsync(ts)).Should().BeEmpty();

        // The file is back in Slack
        _slack.MissingFiles.TryRemove("kapot.jpg", out _);
        var result = await Get<SlackEventRetryService>().RetryAsync(failure.Id);

        result.Resolved.Should().BeTrue(result.Message);
        (await SubmissionsAsync(ts)).Should().ContainSingle().Which.AssignmentCode.Should().Be("3");
        (await PendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewEmoji_OnAMessageWithoutPhoto_ShouldNotBeKeptAsFailed()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Zijn we al klaar?", SlackEvents.Scout, null));
        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "thumbsup"));

        (await PendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ReviewEmoji_OnADeletedMessage_ShouldNotBeKeptAsFailed()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 5", SlackEvents.File("weg.jpg")));
        await _app.SendEventAsync(_client, SlackEvents.Deleted(Channel, ts, "Opdracht 5"));
        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "thumbsup"));

        (await PendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task SlackDeliveringTheEventAgain_ShouldResolveTheSameRow()
    {
        var ts = SlackEvents.Ts();
        var message = SlackEvents.Message(Channel, ts, "Opdracht 4", SlackEvents.File("kapot.jpg"));
        await _app.SendEventAsync(_client, message);
        await _app.SendEventAsync(_client, message);

        var failure = (await PendingAsync()).Should().ContainSingle().Subject;
        failure.Attempts.Should().Be(2);

        _slack.MissingFiles.TryRemove("kapot.jpg", out _);
        await _app.SendEventAsync(_client, message);

        (await PendingAsync()).Should().BeEmpty();
        (await Get<IFailedSlackEventRepository>().GetByIdAsync(failure.Id))!.Status.Should().Be(FailedSlackEventStatus.Resolved);
    }

    [Fact]
    public async Task RetryingAMessage_ShouldApplyTheReviewSlackShowsNow()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 5", SlackEvents.File("kapot.jpg")));
        // The review arrives for a message the app could not store: nothing to review yet, so not a failure of its own
        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "thumbsup"));
        (await PendingAsync()).Should().ContainSingle().Which.Kind.Should().Be(nameof(SlackEventKind.Message));

        // Meanwhile the reviewer changed their mind in Slack
        _slack.Reactions[FakeSlackApi.Key(Channel, ts)] = new object[] { SlackEvents.Reacted("heart", TestApp.Reviewer) };
        _slack.MissingFiles.TryRemove("kapot.jpg", out _);

        (await Get<SlackEventRetryService>().RetryAllPendingAsync()).Should().Be(1);

        (await SubmissionsAsync(ts)).Should().ContainSingle().Which.Status.Should().Be(SubmissionStatus.Awesome);
        (await PendingAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RetryingAReaction_ShouldKeepADashboardOverride()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 6", SlackEvents.File("goed.jpg")));
        var submission = (await SubmissionsAsync(ts)).Should().ContainSingle().Subject;
        await Get<ISubmissionWorkflowService>().OverrideStatusAsync(submission.Id, SubmissionStatus.Rejected, "Fout bestand");

        // A reaction failure stored earlier; Slack shows no review emoji on the message
        await Get<IFailedSlackEventRepository>().RecordFailureAsync(
            $"Reaction:{Channel}:{ts}", nameof(SlackEventKind.ReactionRemoved), Channel, ts, "{}", "Time-out");
        var failure = (await PendingAsync()).Should().ContainSingle().Subject;

        (await Get<SlackEventRetryService>().RetryAsync(failure.Id)).Resolved.Should().BeTrue();

        (await SubmissionsAsync(ts)).Single().Status.Should().Be(SubmissionStatus.Rejected);
    }

    [Fact]
    public async Task Retry_WhileARebuildRuns_ShouldBeRefused()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 7", SlackEvents.File("kapot.jpg")));
        var failure = (await PendingAsync()).Should().ContainSingle().Subject;
        _slack.MissingFiles.TryRemove("kapot.jpg", out _);

        using (await _app.Factory.Services.GetRequiredService<SlackProcessingGate>().CloseAsync())
        {
            var result = await Get<SlackEventRetryService>().RetryAsync(failure.Id);
            result.Resolved.Should().BeFalse();
            result.Message.Should().Contain("rebuild");
        }

        (await SubmissionsAsync(ts)).Should().BeEmpty();
        (await PendingAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_InManualMode_ShouldNotRunAMessageAgain()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 8", SlackEvents.File("kapot.jpg")));
        var failure = (await PendingAsync()).Should().ContainSingle().Subject;
        _slack.MissingFiles.TryRemove("kapot.jpg", out _);
        var downloads = _slack.Calls("file").Count;

        // Manual mode does not remember which files it saved
        _app.Factory.Services.GetRequiredService<RuntimeSettingsService>().SetManualMode(true);
        var result = await Get<SlackEventRetryService>().RetryAsync(failure.Id);

        result.Resolved.Should().BeFalse();
        result.Message.Should().Contain("Handmatige modus");
        _slack.Calls("file").Should().HaveCount(downloads, "nothing is downloaded again");
        (await PendingAsync()).Should().ContainSingle();
    }

    [Fact]
    public async Task Retry_WhileAnotherRetryRuns_ShouldBeRefused()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 9", SlackEvents.File("kapot.jpg")));
        var failure = (await PendingAsync()).Should().ContainSingle().Subject;

        // Another admin's retry holds the lock
        var retryLock = _app.Factory.Services.GetRequiredService<SlackRetryLock>();
        await retryLock.Semaphore.WaitAsync();
        try
        {
            (await Get<SlackEventRetryService>().RetryAsync(failure.Id)).Message.Should().Be(SlackEventRetryService.Busy);
            (await Get<SlackEventRetryService>().RetryAllPendingAsync()).Should().BeNull();
        }
        finally
        {
            retryLock.Semaphore.Release();
        }

        (await Get<IFailedSlackEventRepository>().GetByIdAsync(failure.Id))!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Retry_WithAnUnreadablePayload_ShouldExplainInsteadOfThrowing()
    {
        await Get<IFailedSlackEventRepository>().RecordFailureAsync(
            $"Message:{Channel}:1.0", nameof(SlackEventKind.Message), Channel, "1.0", "{not json", "Time-out");
        var failure = (await PendingAsync()).Should().ContainSingle().Subject;

        var result = await Get<SlackEventRetryService>().RetryAsync(failure.Id);

        result.Resolved.Should().BeFalse();
        result.Message.Should().Contain("kon niet worden gelezen");
    }
}
