using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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

public class SlackCatchUpServiceTests
{
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<ISlackHistoryClient> _history = new();
    private readonly Mock<ISubmissionWorkflowService> _workflow = new();
    private readonly SlackProcessingGate _gate = new();
    private readonly Group _wolves = new() { Name = "De Wolven", SlackChannelId = "C1" };
    private readonly Group _owls = new() { Name = "De Bosuilen", SlackChannelId = "C2" };

    public SlackCatchUpServiceTests()
    {
        _groups.Setup(g => g.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { _wolves, _owls });
        _history.Setup(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<SlackEventItemDto>());
        _workflow.Setup(w => w.ProcessMissedMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProcessSlackMessageResult.Succeeded(new[] { Guid.NewGuid() }));
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
            .AddSingleton(_groups.Object)
            .AddSingleton(_history.Object)
            .AddSingleton(_workflow.Object)
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
        _history.Setup(h => h.GetChannelMessagesAsync("C1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            Upload("C1", "1.0"),
            Upload("C1", "2.0", user: "U_JURY"),                                                // reviewer: never a submission
            new SlackEventItemDto { Type = "message", Channel = "C1", Ts = "3.0", Text = "hoi" }, // no files
            new SlackEventItemDto { Type = "message", Channel = "C1", Ts = "4.0", BotId = "B1", Files = Upload("C1", "4.0").Files }
        });
        _history.Setup(h => h.GetChannelMessagesAsync("C2", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Upload("C2", "5.0") });
        _workflow.Setup(w => w.SyncReviewAsync("C2", "5.0", It.IsAny<IReadOnlyList<SlackMessageReactionDto>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var service = Create();

        service.Request("test");
        await service.Completion;

        _workflow.Verify(w => w.ProcessMissedMessageAsync(It.Is<SlackEventItemDto>(m => m.Ts == "1.0"), It.IsAny<CancellationToken>()), Times.Once);
        _workflow.Verify(w => w.ProcessMissedMessageAsync(It.Is<SlackEventItemDto>(m => m.Ts == "5.0"), It.IsAny<CancellationToken>()), Times.Once);
        _workflow.Verify(w => w.ProcessMissedMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _workflow.Verify(w => w.SyncReviewAsync("C1", "1.0", It.Is<IReadOnlyList<SlackMessageReactionDto>?>(r => r!.Single().Name == "thumbsup"), It.IsAny<CancellationToken>()), Times.Once);
        service.LastResult.Should().BeEquivalentTo(new { MessagesChecked = 2, SubmissionsAdded = 2, ReviewsUpdated = 1 });
    }

    [Fact]
    public async Task Request_ShouldOnlyLookAtToday()
    {
        DateTimeOffset? since = null;
        _history.Setup(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Callback<string, DateTimeOffset, CancellationToken>((_, s, _) => since = s)
            .ReturnsAsync(Array.Empty<SlackEventItemDto>());
        var service = Create();

        service.Request("test");
        await service.Completion;

        var today = DateTimeOffset.UtcNow.ToGameTime();
        since!.Value.ToGameTime().Should().Be(new DateTimeOffset(today.Date, today.Offset));
    }

    [Fact]
    public async Task UnreadableChannel_ShouldNotStopTheOthers()
    {
        _history.Setup(h => h.GetChannelMessagesAsync("C1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SlackApiException("conversations.history", "not_in_channel"));
        _history.Setup(h => h.GetChannelMessagesAsync("C2", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).ReturnsAsync(new[] { Upload("C2", "5.0") });
        var service = Create();

        service.Request("test");
        await service.Completion;

        _workflow.Verify(w => w.ProcessMissedMessageAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()), Times.Once);
        service.LastResult!.ChannelErrors.Should().ContainSingle().Which.Should().Contain("De Wolven").And.Contain("not_in_channel");
    }

    [Fact]
    public async Task RequestsDuringARun_ShouldLeadToOneMoreRun()
    {
        var firstRead = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var reads = 0;
        _history.Setup(h => h.GetChannelMessagesAsync("C1", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                if (Interlocked.Increment(ref reads) == 1)
                {
                    firstRead.SetResult();
                    await release.Task;
                }

                return Array.Empty<SlackEventItemDto>();
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
        _history.Verify(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Never);

        rebuild.Dispose();
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        _history.Verify(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(true, false)]  // simulation: no Slack to read
    [InlineData(false, true)]  // manual mode: would save every file again
    public async Task WithoutSlackOrInManualMode_ShouldNotRead(bool simulation, bool manualMode)
    {
        var service = Create(simulation, manualMode);

        service.Request("test");
        await service.Completion;

        _history.VerifyNoOtherCalls();
    }
}
