using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// With BlockAttemptsAfterApproval on, a group cannot send a new attempt for an assignment it already has approved or awesome.
/// </summary>
public class AttemptAfterApprovalTests : IDisposable
{
    private const string Ts = "1727704800.500000";

    private readonly TempDirectory _temp = new();
    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<IAssignmentRepository> _assignments = new();
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Mock<ISubmissionChangeNotifier> _notifier = new();
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly List<Submission> _existing = new();
    private readonly List<Submission> _added = new();
    private readonly IOptions<ScoutingSettings> _settings;
    private readonly RuntimeSettingsService _runtime;

    public AttemptAfterApprovalTests()
    {
        _settings = Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            BlockAttemptsAfterApproval = true,
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            }
        });
        _runtime = new RuntimeSettingsService(_settings, NullLogger<RuntimeSettingsService>.Instance);

        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _groups.Setup(g => g.GetByIdAsync(_group.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _assignments.Setup(a => a.GetByNumberAsync(It.IsIn(4, 5), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int number, CancellationToken _) => new Assignment { Number = number });
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(new byte[] { 1 }));
        _slack.Setup(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _storage.Setup(s => s.SaveInitialFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string name, Stream _, CancellationToken _) => $"/s/{name}");
        _storage.Setup(s => s.RenameFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string name, CancellationToken _) => $"/s/{name}");
        _submissions.Setup(s => s.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>())).Callback<Submission, CancellationToken>((s, _) => _added.Add(s));
        _submissions.Setup(s => s.GetBySlackMessageIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string channel, string ts, CancellationToken _) => _existing.Where(s => s.SlackChannelId == channel && s.SlackMessageId == ts).ToList());
        _submissions.Setup(s => s.SearchSubmissionsAsync(It.IsAny<SubmissionFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SubmissionFilter f, CancellationToken _) => _existing
                .Where(s => f.GroupId == null || s.GroupId == f.GroupId)
                .Where(s => f.AssignmentNumber == null || s.AssignmentNumber == f.AssignmentNumber)
                .ToList());
    }

    public void Dispose() => _temp.Dispose();

    private SubmissionWorkflowService Create() => new(
        _submissions.Object, _groups.Object, _assignments.Object, new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        _storage.Object, _slack.Object, _notifier.Object, _runtime, _settings,
        NullLogger<SubmissionWorkflowService>.Instance);

    private static SlackEventItemDto Message(string text, string ts = Ts) => new()
    {
        Channel = "C1", User = "U_SCOUT", Text = text, Ts = ts,
        Files = new List<SlackFileItemDto> { new() { Id = "F1", Name = "a.jpg", Size = 1, UrlPrivate = "https://files.slack.com/a.jpg" } }
    };

    private Submission Earlier(int number, SubmissionStatus status, string ts = "1727700000.000100")
    {
        var submission = new Submission
        {
            GroupId = _group.Id, Group = _group, AssignmentNumber = number, Status = status,
            SlackChannelId = "C1", SlackMessageId = ts, FileName = "eerder.jpg", StoragePath = $"/s/eerder-{ts}.jpg"
        };
        _existing.Add(submission);
        return submission;
    }

    private void VerifyBlockedFeedback(int number) =>
        _slack.Verify(s => s.PostThreadReplyAsync("C1", Ts, It.Is<string>(t => t.Contains($"Opdracht {number} is al goedgekeurd")), It.IsAny<CancellationToken>()), Times.Once);

    [Theory]
    [InlineData(SubmissionStatus.Approved)]
    [InlineData(SubmissionStatus.Awesome)]
    public async Task NewAttempt_AfterApproval_ShouldNotBeStoredAndGetFeedback(SubmissionStatus earlier)
    {
        Earlier(4, earlier);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().BeEmpty();
        result.Message.Should().Contain("al goedgekeurd");
        _added.Should().BeEmpty();
        _slack.Verify(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _slack.Verify(s => s.AddReactionAsync("C1", Ts, "lock", It.IsAny<CancellationToken>()), Times.Once);
        VerifyBlockedFeedback(4);
    }

    [Theory]
    [InlineData(SubmissionStatus.Pending)]
    [InlineData(SubmissionStatus.Rejected)]
    public async Task NewAttempt_AfterOpenOrRejected_ShouldBeStored(SubmissionStatus earlier)
    {
        Earlier(4, earlier);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().ContainSingle();
        _slack.Verify(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), "lock", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ApprovalForAnotherAssignment_ShouldNotBlock()
    {
        Earlier(5, SubmissionStatus.Approved);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().ContainSingle();
    }

    [Fact]
    public async Task SwitchedOffInTheApp_ShouldStoreTheAttempt()
    {
        Earlier(4, SubmissionStatus.Approved);
        _runtime.SetBlockAttemptsAfterApproval(false);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().ContainSingle();
    }

    [Fact]
    public async Task SameMessageDeliveredAgain_ShouldNotBlockItself()
    {
        Earlier(4, SubmissionStatus.Approved, ts: Ts);
        _submissions.Setup(s => s.ExistsAsync("C1", Ts, "F1", "a.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.Message.Should().NotContain("al goedgekeurd");
        _slack.Verify(s => s.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualMode_ShouldStillSaveTheFile()
    {
        Earlier(4, SubmissionStatus.Approved);
        _runtime.SetManualMode(true);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.StoredFiles.Should().ContainSingle();
        _slack.Verify(s => s.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EditToACompletedAssignment_ShouldKeepTheOldNumberAndGetFeedback()
    {
        Earlier(5, SubmissionStatus.Approved);
        var mine = Earlier(4, SubmissionStatus.Pending, ts: Ts);

        var result = await Create().ProcessSlackMessageChangedAsync(new SlackEventItemDto
        {
            Channel = "C1",
            Message = new SlackEventItemDto { User = "U_SCOUT", Text = "Opdracht 5", Ts = Ts, Files = Message("").Files },
            PreviousMessage = new SlackEventItemDto { Text = "Opdracht 4", Ts = Ts }
        });

        result.Message.Should().Contain("al goedgekeurd");
        mine.AssignmentNumber.Should().Be(4);
        VerifyBlockedFeedback(5);
    }

    [Fact]
    public async Task Import_ShouldSkipMessagesTheBotBlockedLive()
    {
        var message = Message("Opdracht 4");
        message.Reactions = new List<SlackMessageReactionDto> { new() { Name = "lock", Users = new List<string> { "U_BOT" } } };

        var result = await Create().ImportSlackMessageAsync(message);

        result.CreatedSubmissionIds.Should().BeEmpty();
        _added.Should().BeEmpty();
    }

    [Fact]
    public async Task Import_WithoutTheBotsEmoji_ShouldStoreTheMessage()
    {
        var result = await Create().ImportSlackMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().ContainSingle();
    }
}
