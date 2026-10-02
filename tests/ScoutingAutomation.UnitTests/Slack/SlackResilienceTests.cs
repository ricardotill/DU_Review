using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

/// <summary>What keeps Slack processing going when something fails: a bad event, a slow channel, a stuck rebuild.</summary>
public class SlackResilienceTests
{
    private static SlackEventLog NewLog() => new(NullLogger<SlackEventLog>.Instance);

    [Fact]
    public async Task Queue_EventThatThrows_ShouldNotStopTheLaterEventsOfItsGroup()
    {
        var handled = new List<string>();
        var errors = new List<Exception>();
        var queue = new SlackEventQueue(envelope =>
        {
            if (envelope.Event!.Ts == "1") throw new InvalidOperationException("boom");
            handled.Add(envelope.Event.Ts!);
            return Task.CompletedTask;
        }, (_, ex) => errors.Add(ex));

        queue.Enqueue(new SlackEventEnvelopeDto { Event = new SlackEventItemDto { Channel = "C1", Ts = "1" } });
        queue.Enqueue(new SlackEventEnvelopeDto { Event = new SlackEventItemDto { Channel = "C1", Ts = "2" } });
        await queue.CompleteAsync();

        handled.Should().Equal("2");
        errors.Should().ContainSingle().Which.Message.Should().Be("boom");
    }

    [Fact]
    public void SocketMessage_WithAnUnreadableEvent_ShouldStillBeAcknowledged()
    {
        var json = """{"type":"events_api","envelope_id":"E1","payload":{"event":{"type":"message","files":"not a list"}}}""";

        var message = SlackSocketModeMessage.Parse(Encoding.UTF8.GetBytes(json));

        message.Acknowledgement.Should().Contain("E1", "Slack would only send the same unreadable event again");
        message.Event.Should().BeNull();
        message.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Gate_PauseThatIsCancelled_ShouldLetLiveEventsThroughAgain()
    {
        var gate = new SlackProcessingGate();
        var stuck = await gate.EnterAsync();
        using var giveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var close = () => gate.CloseAsync(giveUp.Token);

        await close.Should().ThrowAsync<OperationCanceledException>();
        gate.IsClosed.Should().BeFalse();
        stuck.Dispose();
        (await gate.EnterAsync().WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
    }

    [Fact]
    public async Task ChannelLocks_ShouldMakeOneChannelTakeTurnsButLeaveOthersFree()
    {
        var locks = new SlackChannelLocks();
        using var first = await locks.EnterAsync("C1");

        var sameChannel = locks.EnterAsync("C1");
        var otherChannel = locks.EnterAsync("C2");

        (await otherChannel.WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
        sameChannel.IsCompleted.Should().BeFalse();
        first.Dispose();
        (await sameChannel.WaitAsync(TimeSpan.FromSeconds(1))).Dispose();
    }

    [Fact]
    public async Task Dispatcher_DeletedMessageWithReplies_ShouldRemoveItsSubmissions()
    {
        var workflow = new Mock<ISubmissionWorkflowService>();
        workflow.Setup(w => w.ProcessSlackMessageDeletedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>(), "weg"));
        var dispatcher = new SlackEventDispatcher(workflow.Object, new SlackProcessingGate(), NewLog(), TimeProvider.System, NullLogger<SlackEventDispatcher>.Instance);

        // Slack keeps a deleted message with thread replies as a "tombstone", and sends that as an edit
        await dispatcher.DispatchAsync(new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto
            {
                Type = "message", Subtype = "message_changed", Channel = "C1",
                Message = new SlackEventItemDto { Subtype = "tombstone", Ts = "1.0", Text = "This message was deleted." }
            }
        });

        workflow.Verify(w => w.ProcessSlackMessageDeletedAsync(It.Is<SlackEventItemDto>(e => e.Channel == "C1" && e.DeletedTs == "1.0"), It.IsAny<CancellationToken>()), Times.Once);
        workflow.Verify(w => w.ProcessSlackMessageChangedAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Dispatcher_MessageWithAFailedDownload_ShouldShowAsFailedOnTheEventsPage()
    {
        var workflow = new Mock<ISubmissionWorkflowService>();
        workflow.Setup(w => w.ProcessSlackMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackMessageResult.Succeeded(new[] { Guid.NewGuid() }, "1 inzending(en) geregistreerd") with { FailedDownloads = new[] { "video.mp4" } });
        var log = NewLog();
        var dispatcher = new SlackEventDispatcher(workflow.Object, new SlackProcessingGate(), log, TimeProvider.System, NullLogger<SlackEventDispatcher>.Instance);

        await dispatcher.DispatchAsync(new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "message", Channel = "C1", Ts = "1.0" } });

        var entry = log.Recent().Single();
        entry.Outcome.Should().Be(SlackEventOutcome.Failed);
        entry.Detail.Should().Contain("video.mp4");
    }

    [Fact]
    public async Task CatchUp_ChannelThatTimesOut_ShouldNotStopTheOtherChannels()
    {
        var (service, history, workflow, log) = CatchUp();
        history.Setup(h => h.GetChannelMessagesAsync("C1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Slack did not answer"));
        history.Setup(h => h.GetChannelMessagesAsync("C2", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Upload("C2", "5.0") });

        service.Request("test");
        await service.Completion;

        workflow.Verify(w => w.ProcessMissedMessageAsync(It.Is<SlackEventItemDto>(m => m.Ts == "5.0"), It.IsAny<CancellationToken>()), Times.Once);
        service.LastResult!.ChannelErrors.Should().ContainSingle().Which.Should().Contain("De Wolven");
        var entry = log.Recent().Single(e => e.Kind == SlackEventKind.CatchUp);
        entry.Outcome.Should().Be(SlackEventOutcome.Failed);
        entry.Detail.Should().Contain("De Wolven");
    }

    [Fact]
    public async Task CatchUp_MessageThatFails_ShouldNotStopTheRest()
    {
        var (service, history, workflow, log) = CatchUp();
        history.Setup(h => h.GetChannelMessagesAsync("C1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Upload("C1", "1.0"), Upload("C1", "2.0") });
        workflow.Setup(w => w.ProcessMissedMessageAsync(It.Is<SlackEventItemDto>(m => m.Ts == "1.0"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("disk full"));

        service.Request("test");
        await service.Completion;

        workflow.Verify(w => w.ProcessMissedMessageAsync(It.Is<SlackEventItemDto>(m => m.Ts == "2.0"), It.IsAny<CancellationToken>()), Times.Once);
        service.LastResult!.ChannelErrors.Should().ContainSingle().Which.Should().Contain("disk full");
    }

    [Fact]
    public async Task CatchUp_WithNothingMissed_ShouldShowThatOnTheEventsPage()
    {
        var (service, _, _, log) = CatchUp();

        service.Request("test");
        await service.Completion;

        var entry = log.Recent().Single();
        entry.Kind.Should().Be(SlackEventKind.CatchUp);
        entry.Outcome.Should().Be(SlackEventOutcome.Processed);
        entry.Detail.Should().Contain("Niets gemist");
    }

    [Fact]
    public void ConnectionStatus_ShouldShowDropsAndReconnectsOnTheEventsPage()
    {
        var log = NewLog();
        var status = new SlackConnectionStatus(log);

        status.Set(SlackConnectionState.Connecting);
        status.Set(SlackConnectionState.Connected);
        status.Set(SlackConnectionState.Connected);
        status.Set(SlackConnectionState.Error, "The remote party closed the WebSocket connection");
        status.Set(SlackConnectionState.Error, "Name or service not known");
        status.Closed("Slack vroeg om opnieuw te verbinden (refresh_requested)");

        log.Recent().Reverse().Select(e => (e.Kind, e.Outcome)).Should().Equal(
            (SlackEventKind.Connection, SlackEventOutcome.Processed),
            (SlackEventKind.Connection, SlackEventOutcome.Failed),
            (SlackEventKind.Connection, SlackEventOutcome.Failed),
            (SlackEventKind.Connection, SlackEventOutcome.Ignored));
        log.Recent()[1].Detail.Should().Contain("Name or service not known");
    }

    [Fact]
    public void ErrorsLoggedAnywhere_ShouldShowOnTheEventsPage()
    {
        var log = NewLog();
        var services = new ServiceCollection().AddSingleton(log).BuildServiceProvider();
        using var provider = new EventLogLoggerProvider(services);
        var logger = provider.CreateLogger("ScoutingAutomation.Infrastructure.Services.GameStartAnnouncer");

        logger.LogWarning("only a warning");
        logger.LogError(new IOException("disk full"), "Posting the {Name} message failed", "game-start");
        logger.LogError(EventLogLoggerProvider.AlreadyShown, "already on the page");

        var entry = log.Recent().Should().ContainSingle().Subject;
        entry.Kind.Should().Be(SlackEventKind.System);
        entry.Outcome.Should().Be(SlackEventOutcome.Failed);
        entry.Text.Should().Be("GameStartAnnouncer");
        entry.Detail.Should().Be("Posting the game-start message failed: disk full");
    }

    [Fact]
    public async Task History_ServerErrorThenAnswer_ShouldTryAgain()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"ok":true,"messages":[{"ts":"1.0","text":"hoi"}]}""", Encoding.UTF8, "application/json") });
        var client = new SlackHistoryClient(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { BotToken = "xoxb-test" } }),
            NullLogger<SlackHistoryClient>.Instance, wait: (_, _) => Task.CompletedTask);

        var messages = await client.GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        messages.Should().ContainSingle();
    }

    [Fact]
    public async Task History_ThreadsWithOnlyTheBotsFeedback_ShouldNotBeRead()
    {
        var handler = new FakeHttpHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("history")
                ? """{"ok":true,"messages":[{"ts":"1.0","thread_ts":"1.0","reply_count":1,"reply_users":["U_JURY"]}]}"""
                : """{"ok":true,"messages":[]}""", Encoding.UTF8, "application/json")
        });
        var settings = Options.Create(new ScoutingSettings { Reviewers = new List<string> { "U_JURY" }, Slack = new SlackSettings { BotToken = "xoxb-test" } });
        var client = new SlackHistoryClient(new HttpClient(handler), settings, NullLogger<SlackHistoryClient>.Instance, TestRuntime.For(settings));

        await client.GetChannelMessagesAsync("C1", DateTimeOffset.UnixEpoch);

        handler.Requests.Should().ContainSingle("a thread with only the jury's remarks holds no submissions");
    }

    [Fact]
    public async Task Sender_StoppedHalfway_ShouldStillReportWhatWasPosted()
    {
        var wolves = new Group { Id = Guid.NewGuid(), Name = "De Wolven", SlackChannelId = "C1" };
        var owls = new Group { Id = Guid.NewGuid(), Name = "De Bosuilen", SlackChannelId = "C2" };
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { wolves, owls });
        using var stop = new CancellationTokenSource();
        var slack = new Mock<ISlackClient>();
        slack.Setup(s => s.PostMessageAsync("C1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => stop.Cancel())
            .ReturnsAsync((string?)null);
        var sender = new GroupMessageSender(groups.Object, slack.Object, NullLogger<GroupMessageSender>.Instance);

        var results = await sender.PostAsync("Hoi", new[] { wolves.Id, owls.Id }, ct: stop.Token);

        results.Select(r => (r.ChannelId, r.Posted, r.Error)).Should().Equal(("C1", true, null), ("C2", false, GroupMessageSender.Cancelled));
        slack.Verify(s => s.PostMessageAsync("C2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Announcer_WithAnEventDateMonthsAhead_ShouldKeepRunning()
    {
        using var temp = new TempDirectory();
        var options = Options.Create(new ScoutingSettings
        {
            EventStart = DateTime.Now.AddDays(100),
            RuntimeSettingsFile = Path.Combine(temp.Path, "runtime-settings.json"),
            GameStartAnnouncement = new GameStartAnnouncementConfig { AutoPost = true, Message = "Start" },
            Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" }
        });
        var services = new ServiceCollection()
            .AddSingleton(Mock.Of<IGroupRepository>())
            .AddSingleton(Mock.Of<ISlackClient>())
            .BuildServiceProvider();
        var announcer = new GameStartAnnouncer(services.GetRequiredService<IServiceScopeFactory>(), options, TestRuntime.For(options),
            TimeProvider.System, NullLogger<GameStartAnnouncer>.Instance);

        await announcer.StartAsync(CancellationToken.None);
        await Task.Delay(200);

        // Task.Delay refuses waits over 49 days; the announcer waits in steps and must not have stopped
        announcer.ExecuteTask!.IsCompleted.Should().BeFalse();
        await announcer.StopAsync(CancellationToken.None);
    }

    private static (SlackCatchUpService Service, Mock<ISlackHistoryClient> History, Mock<ISubmissionWorkflowService> Workflow, SlackEventLog Log) CatchUp()
    {
        var groups = new Mock<IGroupRepository>();
        groups.Setup(g => g.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new Group { Name = "De Wolven", SlackChannelId = "C1" },
            new Group { Name = "De Bosuilen", SlackChannelId = "C2" }
        });
        var history = new Mock<ISlackHistoryClient>();
        history.Setup(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SlackEventItemDto>());
        var workflow = new Mock<ISubmissionWorkflowService>();
        workflow.Setup(w => w.ProcessMissedMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackMessageResult.Succeeded(Array.Empty<Guid>()));
        var settings = Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test", SocketMode = true } });
        var services = new ServiceCollection()
            .AddSingleton(groups.Object)
            .AddSingleton(history.Object)
            .AddSingleton(workflow.Object)
            .BuildServiceProvider();
        var log = NewLog();
        var service = new SlackCatchUpService(services.GetRequiredService<IServiceScopeFactory>(), settings, new SlackProcessingGate(),
            TestRuntime.For(settings), NullLogger<SlackCatchUpService>.Instance, eventLog: log);
        return (service, history, workflow, log);
    }

    private static SlackEventItemDto Upload(string channel, string ts) => new()
    {
        Type = "message", Subtype = "file_share", Channel = channel, Ts = ts, User = "U_SCOUT", Text = "Opdracht 4",
        Files = new List<SlackFileItemDto> { new() { Id = $"F{ts}", Name = "a.jpg" } }
    };
}
