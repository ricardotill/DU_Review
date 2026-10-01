using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackClientTests
{
    private static SlackClient Create(FakeHttpHandler handler, bool simulation = false, string? token = "xoxb-test") =>
        new(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = simulation, BotToken = token } }),
            NullLogger<SlackClient>.Instance);

    private static async Task<byte[]> ReadAll(Stream stream)
    {
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(true, "xoxb-test", "https://files.slack.com/a.jpg")]
    [InlineData(false, null, "https://files.slack.com/a.jpg")]
    [InlineData(false, "xoxb-test", "sim://a.jpg")]
    public async Task DownloadFileAsync_SimulatedCases_ShouldNotCallSlack(bool simulation, string? token, string url)
    {
        var handler = FakeHttpHandler.Json("{}");

        var bytes = await ReadAll(await Create(handler, simulation, token).DownloadFileAsync(url, "a.jpg"));

        handler.Requests.Should().BeEmpty();
        bytes.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47); // PNG
    }

    [Fact]
    public async Task DownloadFileAsync_ShouldDownloadWithBotToken()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });

        var bytes = await ReadAll(await Create(handler).DownloadFileAsync("https://files.slack.com/a.jpg", "a.jpg"));

        bytes.Should().Equal(1, 2, 3);
        handler.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer xoxb-test");
    }

    [Fact]
    public async Task DownloadFileAsync_WhenSlackFails_ShouldThrowInsteadOfStoringAPlaceholder()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var act = () => Create(handler).DownloadFileAsync("https://files.slack.com/doc.pdf", "doc.pdf");

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task DownloadFileAsync_SimulatedDocument_ShouldGetTextContent()
    {
        var bytes = await ReadAll(await Create(FakeHttpHandler.Json("{}"), simulation: true).DownloadFileAsync("sim://doc.pdf", "doc.pdf"));

        Encoding.UTF8.GetString(bytes).Should().StartWith("Scouting Submission File: doc.pdf");
    }

    [Fact]
    public async Task DownloadFileAsync_WithoutFileName_ShouldUseDefaultName()
    {
        var bytes = await ReadAll(await Create(FakeHttpHandler.Json("{}"), simulation: true).DownloadFileAsync("sim://x"));

        bytes.Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task AddReactionAsync_ShouldPostToSlackWithoutColons()
    {
        var handler = FakeHttpHandler.Json("""{"ok":true}""");

        var added = await Create(handler).AddReactionAsync("C1", "1.0", ":warning:");

        added.Should().BeTrue();
        var (request, body) = handler.Requests.Single();
        request.RequestUri!.ToString().Should().Be("https://slack.com/api/reactions.add");
        request.Headers.Authorization!.ToString().Should().Be("Bearer xoxb-test");
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("channel").GetString().Should().Be("C1");
        json.RootElement.GetProperty("timestamp").GetString().Should().Be("1.0");
        json.RootElement.GetProperty("name").GetString().Should().Be("warning");
    }

    [Fact]
    public async Task AddReactionAsync_AlreadyReacted_ShouldReturnFalse()
    {
        var added = await Create(FakeHttpHandler.Json("""{"ok":false,"error":"already_reacted"}""")).AddReactionAsync("C1", "1.0", "warning");

        added.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"missing_scope"}""", HttpStatusCode.OK)]
    [InlineData("""{"ok":false}""", HttpStatusCode.OK)]
    [InlineData("oops", HttpStatusCode.InternalServerError)]
    public async Task AddReactionAsync_OtherFailures_ShouldNotThrowAndReturnTrue(string response, HttpStatusCode status)
    {
        var added = await Create(FakeHttpHandler.Json(response, status)).AddReactionAsync("C1", "1.0", "warning");

        added.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, "xoxb-test")]
    [InlineData(false, "")]
    public async Task SlackCalls_InSimulation_ShouldNotCallSlack(bool simulation, string token)
    {
        var handler = FakeHttpHandler.Json("{}");
        var client = Create(handler, simulation, token);

        (await client.AddReactionAsync("C1", "1.0", "warning")).Should().BeTrue();
        await client.PostThreadReplyAsync("C1", "1.0", "hallo");
        (await client.GetReactionsAsync("C1", "1.0")).Should().BeNull("reactions cannot be read without Slack");

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReactionsAsync_ShouldReturnCurrentReactions()
    {
        var handler = FakeHttpHandler.Json("""{"ok":true,"type":"message","message":{"ts":"1.0","reactions":[{"name":"thumbsup","users":["U_JURY"],"count":1}]}}""");

        var reactions = await Create(handler).GetReactionsAsync("C1", "1.0");

        reactions.Should().ContainSingle().Which.Users.Should().Equal("U_JURY");
        var request = handler.Requests.Single().Request;
        request.Method.Should().Be(HttpMethod.Get);
        request.RequestUri!.AbsoluteUri.Should().Be("https://slack.com/api/reactions.get?channel=C1&timestamp=1.0&full=true");
        request.Headers.Authorization!.ToString().Should().Be("Bearer xoxb-test");
    }

    [Theory]
    [InlineData("""{"ok":true,"message":{"ts":"1.0"}}""")]
    [InlineData("""{"ok":true}""")]
    public async Task GetReactionsAsync_MessageWithoutReactions_ShouldBeEmpty(string response)
    {
        (await Create(FakeHttpHandler.Json(response)).GetReactionsAsync("C1", "1.0")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"message_not_found"}""", HttpStatusCode.OK)]
    [InlineData("null", HttpStatusCode.OK)]
    [InlineData("", HttpStatusCode.InternalServerError)]
    public async Task GetReactionsAsync_Failure_ShouldReturnNull(string response, HttpStatusCode status)
    {
        (await Create(FakeHttpHandler.Json(response, status)).GetReactionsAsync("C1", "1.0")).Should().BeNull();
    }

    [Fact]
    public async Task GetReactionsAsync_Cancelled_ShouldThrow()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Create(FakeHttpHandler.Json("{}")).GetReactionsAsync("C1", "1.0", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PostThreadReplyAsync_ShouldPostInThread()
    {
        var handler = FakeHttpHandler.Json("""{"ok":true}""");

        await Create(handler).PostThreadReplyAsync("C1", "1.0", "Opdracht 999 bestaat niet");

        var (request, body) = handler.Requests.Single();
        request.RequestUri!.ToString().Should().Be("https://slack.com/api/chat.postMessage");
        using var json = JsonDocument.Parse(body!);
        json.RootElement.GetProperty("thread_ts").GetString().Should().Be("1.0");
        json.RootElement.GetProperty("text").GetString().Should().Be("Opdracht 999 bestaat niet");
    }
}

public class SlackSocketConnectorTests
{
    [Fact]
    public async Task OpenConnectionAsync_ShouldReturnSocketUrlUsingAppToken()
    {
        var handler = FakeHttpHandler.Json("""{"ok":true,"url":"wss://wss-primary.slack.com/link/?ticket=abc"}""");

        var url = await new SlackSocketConnector(new HttpClient(handler)).OpenConnectionAsync("xapp-1");

        url.Should().Be(new Uri("wss://wss-primary.slack.com/link/?ticket=abc"));
        var request = handler.Requests.Single().Request;
        request.Method.Should().Be(HttpMethod.Post);
        request.RequestUri!.ToString().Should().Be("https://slack.com/api/apps.connections.open");
        request.Headers.Authorization!.ToString().Should().Be("Bearer xapp-1");
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"invalid_auth"}""", "invalid_auth")]
    [InlineData("""{"ok":false}""", "unknown")]
    public async Task OpenConnectionAsync_SlackRefuses_ShouldThrowWithSlackError(string response, string error)
    {
        var act = () => new SlackSocketConnector(new HttpClient(FakeHttpHandler.Json(response))).OpenConnectionAsync("xapp-1");

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage($"*{error}*");
    }

    [Fact]
    public async Task OpenConnectionAsync_HttpError_ShouldThrow()
    {
        var act = () => new SlackSocketConnector(new HttpClient(FakeHttpHandler.Json("", HttpStatusCode.BadGateway))).OpenConnectionAsync("xapp-1");

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}

public class SlackEventDispatcherTests
{
    private readonly Mock<ISubmissionWorkflowService> _workflow = new();
    private readonly SlackEventLog _log = new(NullLogger<SlackEventLog>.Instance);

    private SlackEventDispatcher Create() => new(_workflow.Object, new SlackProcessingGate(), _log, TimeProvider.System, NullLogger<SlackEventDispatcher>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("file_share")]
    public async Task DispatchAsync_Message_ShouldProcessMessage(string? subtype)
    {
        var expected = ProcessSlackMessageResult.Succeeded(new[] { Guid.NewGuid() });
        _workflow.Setup(w => w.ProcessSlackMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "message", Subtype = subtype }
        });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_Reaction_ShouldProcessReaction()
    {
        var expected = ProcessSlackReactionResult.Ignored("x");
        _workflow.Setup(w => w.ProcessSlackReactionAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "reaction_added" }
        });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_RemovedReaction_ShouldProcessRemoval()
    {
        var expected = ProcessSlackReactionResult.Ignored("y");
        _workflow.Setup(w => w.ProcessSlackReactionRemovedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "reaction_removed", Reaction = "thumbsup" }
        });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_EditedMessage_ShouldProcessEdit()
    {
        var expected = ProcessSlackMessageResult.Ignored("edit");
        _workflow.Setup(w => w.ProcessSlackMessageChangedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "message", Subtype = "message_changed", Channel = "C1" }
        });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_DeletedMessage_ShouldProcessDeletion()
    {
        var expected = ProcessSlackMessageResult.Ignored("delete");
        _workflow.Setup(w => w.ProcessSlackMessageDeletedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "message", Subtype = "message_deleted", Channel = "C1", DeletedTs = "1.0" }
        });

        result.Should().Be(expected);
    }

    [Fact]
    public async Task DispatchAsync_ThreadReplyAlsoSentToChannel_ShouldProcessAsMessage()
    {
        var expected = ProcessSlackMessageResult.Ignored("broadcast");
        _workflow.Setup(w => w.ProcessSlackMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var result = await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "message", Subtype = "thread_broadcast", Channel = "C1" }
        });

        result.Should().Be(expected);
    }

    public static IEnumerable<object[]> IrrelevantEnvelopes() => new[]
    {
        new object[] { new SlackEventEnvelopeDto { Type = "app_rate_limited" } },
        new object[] { new SlackEventEnvelopeDto { Type = "event_callback" } },
        new object[] { new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "message", Subtype = "channel_join" } } },
        new object[] { new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "pin_added" } } }
    };

    [Theory]
    [MemberData(nameof(IrrelevantEnvelopes))]
    public async Task DispatchAsync_IrrelevantEnvelope_ShouldReturnNullWithoutProcessing(SlackEventEnvelopeDto envelope)
    {
        var result = await Create().DispatchAsync(envelope);

        result.Should().BeNull();
        _workflow.VerifyNoOtherCalls();
        _log.Recent().Should().BeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_Message_ShouldRecordEventWithOutcome()
    {
        var id = Guid.NewGuid();
        _workflow.Setup(w => w.ProcessSlackMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackMessageResult.Succeeded(new[] { id }, "1 bestand opgeslagen"));

        await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto
            {
                Type = "message", Subtype = "file_share", Channel = "C1", User = "U1", Text = "Opdracht 3",
                Files = new List<SlackFileItemDto> { new() { Name = "a.jpg" } }
            }
        });

        var entry = _log.Recent().Should().ContainSingle().Subject;
        entry.Kind.Should().Be(SlackEventKind.Message);
        entry.Outcome.Should().Be(SlackEventOutcome.Processed);
        entry.Channel.Should().Be("C1");
        entry.User.Should().Be("U1");
        entry.Text.Should().Be("Opdracht 3");
        entry.FileCount.Should().Be(1);
        entry.Detail.Should().Be("1 bestand opgeslagen");
        entry.SubmissionIds.Should().Equal(id);
    }

    [Fact]
    public async Task DispatchAsync_IgnoredAndFailedReactions_ShouldBeRecordedApart()
    {
        _workflow.Setup(w => w.ProcessSlackReactionAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackReactionResult.Ignored("geen jurylid"));
        _workflow.Setup(w => w.ProcessSlackReactionRemovedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackReactionResult.Failed("niet gevonden"));

        await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "reaction_added", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C2" } }
        });
        await Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "reaction_removed", Reaction = "x" }
        });

        var entries = _log.Recent();
        entries.Select(e => (e.Kind, e.Outcome)).Should().Equal(
            (SlackEventKind.ReactionRemoved, SlackEventOutcome.Failed),
            (SlackEventKind.ReactionAdded, SlackEventOutcome.Ignored));
        entries[1].Channel.Should().Be("C2");
        entries[1].Reaction.Should().Be("thumbsup");
    }

    [Fact]
    public async Task DispatchAsync_WorkflowThrows_ShouldRecordFailureAndRethrow()
    {
        _workflow.Setup(w => w.ProcessSlackMessageDeletedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Create().DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto { Type = "message", Subtype = "message_deleted", Channel = "C1" }
        });

        await act.Should().ThrowAsync<InvalidOperationException>();
        var entry = _log.Recent().Should().ContainSingle().Subject;
        entry.Kind.Should().Be(SlackEventKind.Deleted);
        entry.Outcome.Should().Be(SlackEventOutcome.Failed);
        entry.Detail.Should().Contain("boom");
    }
}
