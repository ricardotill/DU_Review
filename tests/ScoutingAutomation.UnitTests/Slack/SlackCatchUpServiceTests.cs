using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackCatchUpServiceTests
{
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly ISlackHistoryClient _history = Substitute.For<ISlackHistoryClient>();
    private readonly ISubmissionWorkflowService _workflow = Substitute.For<ISubmissionWorkflowService>();
    private readonly SlackProcessingGate _gate = new();
    private readonly Group _wolves = new() { Name = "De Wolven", SlackChannelId = "C1" };
    private readonly Group _owls = new() { Name = "De Bosuilen", SlackChannelId = "C2" };

    public SlackCatchUpServiceTests()
    {
        _groups.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { _wolves, _owls });
        _history.GetChannelMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SlackEventItemDto>());
        _workflow.ProcessMissedMessageAsync(Arg.Any<SlackEventItemDto>(), Arg.Any<CancellationToken>())
            .Returns(ProcessSlackMessageResult.Succeeded(new[] { Guid.NewGuid() }));
    }

    private SlackCatchUpService Create(bool simulation = false, bool manualMode = false)
    {
        var settings = Options.Create(new ScoutingSettings
        {
            ManualMode = manualMode,
            Reviewers = new List<string> { "U_JURY" },
            Slack = new SlackSettings { SimulationMode = simulation, BotToken = "xoxb-test", SocketMode = true }
        });
        var services = new ServiceCollection()
            .AddSingleton(_groups)
            .AddSingleton(_history)
            .AddSingleton(_workflow)
            .BuildServiceProvider();
        return new SlackCatchUpService(services.GetRequiredService<IServiceScopeFactory>(), settings, _gate,
            TestRuntime.For(settings), NullLogger<SlackCatchUpService>.Instance);
    }

    private static SlackEventItemDto Upload(string channel, string ts, string user = "U_SCOUT") => new()
    {
        Type = "message", Subtype = "file_share", Channel = channel, Ts = ts, User = user, Text = "Opdracht 4",
        Files = new List<SlackFileItemDto> { new() { Id = $"F{ts}", Name = "a.jpg" } },
        Reactions = new List<SlackMessageReactionDto> { new() { Name = "thumbsup", Users = new List<string> { "U_JURY" } } }
    };

    [Fact]
    public async Task Request_ShouldProcessTodaysUploadsOfEveryGroupAndSyncTheirReviews()
    {
        _history.GetChannelMessagesAsync("C1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new[]
        {
            Upload("C1", "1.0"),
            Upload("C1", "2.0", user: "U_JURY"),                                                // reviewer: never a submission
            new SlackEventItemDto { Type = "message", Channel = "C1", Ts = "3.0", Text = "hoi" }, // no files
            new SlackEventItemDto { Type = "message", Channel = "C1", Ts = "4.0", BotId = "B1", Files = Upload("C1", "4.0").Files }
        });
        _history.GetChannelMessagesAsync("C2", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(new[] { Upload("C2", "5.0") });
        _workflow.SyncReviewAsync("C2", "5.0", Arg.Any<IReadOnlyList<SlackMessageReactionDto>?>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = Create();

        service.Request("test");
        await service.Completion;

        await _workflow.Received(1).ProcessMissedMessageAsync(Arg.Is<SlackEventItemDto>(m => m.Ts == "1.0"), Arg.Any<CancellationToken>());
        await _workflow.Received(1).ProcessMissedMessageAsync(Arg.Is<SlackEventItemDto>(m => m.Ts == "5.0"), Arg.Any<CancellationToken>());
        await _workflow.Received(2).ProcessMissedMessageAsync(Arg.Any<SlackEventItemDto>(), Arg.Any<CancellationToken>());
        await _workflow.Received(1).SyncReviewAsync("C1", "1.0", Arg.Is<IReadOnlyList<SlackMessageReactionDto>?>(r => r!.Single().Name == "thumbsup"), Arg.Any<CancellationToken>());
        service.LastResult.Should().BeEquivalentTo(new { MessagesChecked = 2, SubmissionsAdded = 2, ReviewsUpdated = 1 });
    }

    [Fact]
    public async Task Request_ShouldOnlyLookAtToday()
    {
        DateTimeOffset? since = null;
        _history.GetChannelMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SlackEventItemDto>())
            .AndDoes(ci => since = ci.ArgAt<DateTimeOffset>(1));
        var service = Create();

        service.Request("test");
        await service.Completion;

        var today = DateTimeOffset.UtcNow.ToGameTime();
        since!.Value.ToGameTime().Should().Be(new DateTimeOffset(today.Date, today.Offset));
    }

    [Fact]
    public async Task UnreadableChannel_ShouldNotStopTheOthers()
    {
        _history.GetChannelMessagesAsync("C1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SlackApiException("conversations.history", "not_in_channel"));
        _history.GetChannelMessagesAsync("C2", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(new[] { Upload("C2", "5.0") });
        var service = Create();

        service.Request("test");
        await service.Completion;

        await _workflow.Received(1).ProcessMissedMessageAsync(Arg.Any<SlackEventItemDto>(), Arg.Any<CancellationToken>());
        service.LastResult!.ChannelErrors.Should().ContainSingle().Which.Should().Contain("De Wolven").And.Contain("not_in_channel");
    }

    [Fact]
    public async Task RequestsDuringARun_ShouldLeadToOneMoreRun()
    {
        var firstRead = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reads = 0;
        _history.GetChannelMessagesAsync("C1", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstRead.SetResult();
                    await release.Task;
                }

                return (IReadOnlyList<SlackEventItemDto>)Array.Empty<SlackEventItemDto>();
            });
        var service = Create();

        service.Request("connected");
        await firstRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.Request("reconnected");
        service.Request("reconnected again");
        release.SetResult();
        await service.Completion;

        reads.Should().Be(2);
    }

    [Fact]
    public async Task WhileRebuilding_ShouldWaitUntilTheRebuildIsDone()
    {
        var rebuild = await _gate.CloseAsync();
        var service = Create();

        service.Request("test");
        await Task.Delay(50);
        await _history.DidNotReceive().GetChannelMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        rebuild.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await _history.Received(2).GetChannelMessagesAsync(Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(true, false)]  // simulation: no Slack to read
    [InlineData(false, true)]  // manual mode: would save every file again
    public async Task WithoutSlackOrInManualMode_ShouldNotRead(bool simulation, bool manualMode)
    {
        var service = Create(simulation, manualMode);

        service.Request("test");
        await service.Completion;

        _history.ReceivedCalls().Should().BeEmpty();
    }
}
