using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Infrastructure;

public class SlackTimestampTests
{
    [Fact]
    public void ToDateTimeOffset_ShouldReadSlackSecondsWithFraction()
    {
        SlackTimestamp.ToDateTimeOffset("1727704800.123456").Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1727704800123));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("gisteren")]
    [InlineData("-5")]
    [InlineData("1727704800123456789")]
    public void ToDateTimeOffset_Unusable_ShouldBeNull(string? ts)
    {
        SlackTimestamp.ToDateTimeOffset(ts).Should().BeNull();
    }

    [Fact]
    public void FromDateTimeOffset_ShouldGiveWholeSeconds()
    {
        SlackTimestamp.FromDateTimeOffset(DateTimeOffset.FromUnixTimeSeconds(1727704800).AddMilliseconds(700)).Should().Be("1727704800");
    }
}

public class ReviewPolicyTests
{
    private static readonly ScoutingSettings Settings = new()
    {
        Reviewers = new List<string> { " U_JURY " },
        ReviewStatusReactions = new ReviewStatusReactionsConfig
        {
            Approved = new List<string> { "thumbsup" },
            Rejected = new List<string> { ":x:" },
            Awesome = new List<string> { "heart" }
        }
    };

    [Theory]
    [InlineData(":THUMBSUP:", SubmissionStatus.Approved)]
    [InlineData("x", SubmissionStatus.Rejected)]
    [InlineData("heart", SubmissionStatus.Awesome)]
    [InlineData("tada", null)]
    [InlineData("::", null)]
    [InlineData(null, null)]
    public void StatusFor(string? reaction, SubmissionStatus? expected) => ReviewPolicy.StatusFor(Settings, reaction).Should().Be(expected);

    [Fact]
    public void StatusFor_WithoutReactionConfig_ShouldBeNull() =>
        ReviewPolicy.StatusFor(new ScoutingSettings { ReviewStatusReactions = null! }, "thumbsup").Should().BeNull();
}

public class SlackHistoryClientTests
{
    private static SlackHistoryClient Create(FakeHttpHandler handler) =>
        new(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { BotToken = "xoxb-test" } }),
            NullLogger<SlackHistoryClient>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task GetChannelMessagesAsync_ShouldReadAllPagesAndThreadsInOrder()
    {
        var handler = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("conversations.history") && !url.Contains("cursor="))
                return Json("""{"ok":true,"messages":[{"ts":"300.0","text":"derde"},{"ts":"100.0","text":"thread","thread_ts":"100.0","reply_count":2}],"response_metadata":{"next_cursor":"page 2"}}""");
            if (url.Contains("conversations.history"))
                return Json("""{"ok":true,"messages":[{"ts":"150.0","text":"broadcast"}],"response_metadata":{"next_cursor":""}}""");
            return Json("""{"ok":true,"messages":[{"ts":"100.0","text":"thread"},{"ts":"120.0","text":"antwoord"},{"ts":"150.0","text":"broadcast"}]}""");
        });

        var messages = await Create(handler).GetChannelMessagesAsync("C1", DateTimeOffset.FromUnixTimeSeconds(50));

        messages.Select(m => m.Text).Should().Equal("thread", "antwoord", "broadcast", "derde");
        messages.Should().OnlyContain(m => m.Channel == "C1");
        var urls = handler.Requests.Select(r => r.Request.RequestUri!.AbsoluteUri).ToList();
        urls[0].Should().Be("https://slack.com/api/conversations.history?channel=C1&oldest=50&limit=200");
        urls[1].Should().EndWith("&cursor=page%202");
        urls[2].Should().Be("https://slack.com/api/conversations.replies?channel=C1&ts=100.0&limit=200");
        handler.Requests.Should().OnlyContain(r => r.Request.Headers.Authorization!.ToString() == "Bearer xoxb-test");
    }

    [Fact]
    public async Task GetChannelMessagesAsync_RateLimited_ShouldWaitAndRetry()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            if (calls++ == 0)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return limited;
            }

            return Json("""{"ok":true}""");
        });

        var messages = await Create(handler).GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        messages.Should().BeEmpty();
        calls.Should().Be(2);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_RateLimitedTooOften_ShouldGiveUp()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return limited;
        });

        var act = () => Create(handler).GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(6);
    }

    [Fact]
    public async Task GetChannelMessagesAsync_RateLimitedWithoutRetryAfter_ShouldWaitOneSecond()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => calls++ == 0 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) : Json("""{"ok":true,"messages":[]}"""));
        var started = DateTime.UtcNow;

        await Create(handler).GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        (DateTime.UtcNow - started).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"not_in_channel"}""", "not_in_channel")]
    [InlineData("""{"ok":false}""", "unknown")]
    [InlineData("null", "unknown")]
    public async Task GetChannelMessagesAsync_SlackError_ShouldThrowWithReason(string response, string reason)
    {
        var act = () => Create(new FakeHttpHandler(_ => Json(response))).GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        var error = (await act.Should().ThrowAsync<SlackApiException>()).Which;
        error.Method.Should().Be("conversations.history");
        error.Error.Should().Be(reason);
        error.Message.Should().Be($"Slack conversations.history failed: {reason}");
    }
}

public class SlackProcessingGateTests
{
    [Fact]
    public async Task LiveEvents_ShouldRunSideBySideWhileOpen()
    {
        var gate = new SlackProcessingGate();

        using var first = await gate.EnterAsync();
        using var second = await gate.EnterAsync();

        gate.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task Close_ShouldWaitForRunningEventsAndHoldNewOnes()
    {
        var gate = new SlackProcessingGate();
        var running = await gate.EnterAsync();

        var closing = gate.CloseAsync();
        await Task.Delay(50);
        closing.IsCompleted.Should().BeFalse("an event is still running");

        running.Dispose();
        running.Dispose(); // releasing twice has no extra effect
        var closed = await closing;
        gate.IsClosed.Should().BeTrue();

        var waiting = gate.EnterAsync();
        await Task.Delay(50);
        waiting.IsCompleted.Should().BeFalse("the rebuild holds the gate");

        closed.Dispose();
        (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
        gate.IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task CloseTwice_ShouldThrow()
    {
        var gate = new SlackProcessingGate();
        using var closed = await gate.CloseAsync();

        var act = () => gate.CloseAsync();

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task WaitingEvent_ShouldStopWhenCancelled()
    {
        var gate = new SlackProcessingGate();
        using var closed = await gate.CloseAsync();
        using var cancel = new CancellationTokenSource(50);

        var act = () => gate.EnterAsync(cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Dispatcher_ShouldWaitWhileRebuildRuns()
    {
        var gate = new SlackProcessingGate();
        var workflow = new Moq.Mock<ISubmissionWorkflowService>();
        var dispatcher = new SlackEventDispatcher(workflow.Object, gate, NullLogger<SlackEventDispatcher>.Instance);
        var closed = await gate.CloseAsync();

        var dispatching = dispatcher.DispatchAsync(new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "reaction_added" } });
        await Task.Delay(50);
        workflow.Invocations.Should().BeEmpty();

        closed.Dispose();
        await dispatching.WaitAsync(TimeSpan.FromSeconds(5));
        workflow.Invocations.Should().ContainSingle();
    }
}

public class StorageBackupAndRepositoryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task MoveAllToBackupAsync_ShouldMoveGroupFoldersIntoANewBackupEachTime()
    {
        var storage = new LocalFileStorageService(Options.Create(new ScoutingSettings { StorageRoot = _temp.Path }), NullLogger<LocalFileStorageService>.Instance);
        await storage.SaveInitialFileAsync("De Wolven", "a.jpg", new MemoryStream(new byte[] { 1 }));
        await storage.EnsureFolderStructureAsync("De Bosuilen");

        var first = await storage.MoveAllToBackupAsync();
        await storage.SaveInitialFileAsync("De Wolven", "b.jpg", new MemoryStream(new byte[] { 2 }));
        var second = await storage.MoveAllToBackupAsync();

        File.Exists(Path.Combine(first, "de-wolven", "a.jpg")).Should().BeTrue();
        Directory.Exists(Path.Combine(first, "de-bosuilen", "approved")).Should().BeTrue();
        File.Exists(Path.Combine(second, "de-wolven", "b.jpg")).Should().BeTrue();
        second.Should().NotBe(first);
        Directory.GetDirectories(_temp.Path).Select(Path.GetFileName).Should().Equal("_backup");
    }

    [Fact]
    public async Task Repository_ShouldReturnAllFilesOfAMessageAndDeleteEverything()
    {
        await using var db = TestDb.Create();
        var group = new Group { Name = "G", SlackChannelId = "C" };
        db.Groups.Add(group);
        var b = new Submission { GroupId = group.Id, SlackChannelId = "C", SlackMessageId = "1.0", FileName = "b.jpg" };
        b.Reviews.Add(new Review { Reviewer = "U", Status = SubmissionStatus.Approved });
        db.Submissions.AddRange(b, new Submission { GroupId = group.Id, SlackChannelId = "C", SlackMessageId = "1.0", FileName = "a.jpg" });
        await db.SaveChangesAsync();
        var repo = new SubmissionRepository(db);

        (await repo.GetBySlackMessageIdAsync("C", "1.0")).Select(s => s.FileName).Should().Equal("a.jpg", "b.jpg");

        await repo.DeleteAllAsync();

        db.Submissions.Should().BeEmpty();
        db.Reviews.Should().BeEmpty();
        db.Groups.Should().ContainSingle();
    }

    public void Dispose() => _temp.Dispose();
}
