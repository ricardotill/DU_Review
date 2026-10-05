using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Rebuilding from Slack in the running app, against a fake Slack API.
/// </summary>
public class SlackRebuildIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow.AddHours(-1);
    private readonly TaskCompletionSource _releaseHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _historyRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;

    public SlackRebuildIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack);
    }

    /// <summary>What Slack holds, set once the app's own catch-up at startup is done, so only the rebuild reads it.</summary>
    private async Task FillSlackAsync()
    {
        await _app.Factory.Services.GetRequiredService<ISlackCatchUpService>().Completion.WaitAsync(TimeSpan.FromSeconds(10));
        _slack.History["C01ZEEARENDEN"] = new[]
        {
            SlackEvents.HistoryMessage(Ts(10), "Opdracht 5", new[] { SlackEvents.File("zee.jpg") }, new[] { SlackEvents.Reacted("heart", TestApp.Reviewer) }),
            new { ts = Ts(20), thread_ts = Ts(20), reply_count = 1, user = SlackEvents.Scout, text = "Opdracht 6", files = new[] { SlackEvents.File("draad.jpg") } }
        };
        _slack.Replies[Ts(20)] = new object[]
        {
            new { ts = Ts(20), thread_ts = Ts(20), user = SlackEvents.Scout, text = "Opdracht 6" },
            new { ts = Ts(30), thread_ts = Ts(20), user = SlackEvents.Scout, text = "Opdracht 7", files = new[] { SlackEvents.File("antwoord.jpg") } }
        };
        _slack.BeforeHistory = async channel =>
        {
            if (channel != "C01ZEEARENDEN") return;
            _historyRequested.TrySetResult();
            await _releaseHistory.Task;
        };
    }

    private string Ts(int seconds) => $"{_since.AddSeconds(seconds).ToUnixTimeSeconds()}.000100";

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Rebuild_ShouldReplaceDataWithSlackAndHoldLiveEventsUntilDone()
    {
        using var client = _app.CreateClient();
        await FillSlackAsync();
        var repository = () => _app.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<ISubmissionRepository>();
        (await repository().SearchSubmissionsAsync(new SubmissionFilter())).Should().BeEmpty("live mode never adds sample data");

        // Something the app registered earlier, which the rebuild must back up and replace
        var storage = _app.Factory.Services.GetRequiredService<IFileStorageService>();
        var earlierPath = await storage.SaveInitialFileAsync("De Bosuilen", "DBU 1.jpg", new MemoryStream(new byte[] { 9 }));
        var bosuilen = await _app.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync("C02BOSUILEN");
        await repository().AddAsync(new Domain.Entities.Submission
        {
            GroupId = bosuilen!.Id, AssignmentCode = "1", FileName = "oud.jpg", StoragePath = earlierPath,
            SlackChannelId = "C02BOSUILEN", SlackMessageId = "1.0"
        });

        var rebuild = _app.Factory.Services.GetRequiredService<ISlackRebuildService>();
        rebuild.IsAvailable.Should().BeTrue();

        // Start the rebuild; the fake Slack holds the first history request open
        rebuild.TryStart(_since).Should().BeTrue();
        await _historyRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A live upload arrives meanwhile: it must wait for the rebuild, not be lost or wiped
        var liveTs = Ts(900);
        var live = _app.SendEventAsync(client, SlackEvents.Message("C04POOLVOSSEN", liveTs, "Opdracht 8", SlackEvents.File("live.jpg")));
        await Task.Delay(300);
        live.IsCompleted.Should().BeFalse("live processing is paused during the rebuild");

        _releaseHistory.SetResult();
        await rebuild.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        (await live.WaitAsync(TimeSpan.FromSeconds(30))).GetProperty("success").GetBoolean().Should().BeTrue();

        var progress = rebuild.Progress;
        progress.State.Should().Be(RebuildState.Completed, progress.Error);
        progress.SubmissionsImported.Should().Be(3);
        progress.ReviewsApplied.Should().Be(1);

        var all = await repository().SearchSubmissionsAsync(new SubmissionFilter());
        all.Select(s => s.FileName).Should().BeEquivalentTo("zee.jpg", "draad.jpg", "antwoord.jpg", "live.jpg");

        var awesome = all.Single(s => s.FileName == "zee.jpg");
        awesome.Status.Should().Be(SubmissionStatus.Awesome);
        awesome.StoragePath.Should().EndWith(Path.Combine("de-zeearenden", "awesome", $"DZA 5 {_since.AddSeconds(10).ToLocalTime():HH-mm-ss}.jpg"));
        (await File.ReadAllBytesAsync(awesome.StoragePath)).Should().Equal(1, 2, 3);
        all.Single(s => s.FileName == "antwoord.jpg").AssignmentCode.Should().Be("7", "thread replies are imported too");

        (await File.ReadAllTextAsync(Path.Combine(progress.BackupFolder!, "submissions.json"))).Should().Contain("oud.jpg");
        File.Exists(Path.Combine(progress.BackupFolder!, "de-bosuilen", "DBU 1.jpg")).Should().BeTrue("the earlier file was backed up");
        File.Exists(earlierPath).Should().BeFalse();

        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        (await admin.GetStringAsync("/configuration")).Should().Contain("Opnieuw opbouwen uit Slack");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
