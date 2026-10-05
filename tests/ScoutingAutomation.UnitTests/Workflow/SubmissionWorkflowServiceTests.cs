using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

public class SubmissionWorkflowServiceTests
{
    private readonly Mock<ISubmissionRepository> _subRepoMock = new();
    private readonly Mock<IGroupRepository> _groupRepoMock = new();
    private readonly Mock<IAssignmentRepository> _assignmentRepoMock = new();
    private readonly Mock<IAssignmentParser> _parserMock = new();
    private readonly Mock<IFileStorageService> _storageMock = new();
    private readonly Mock<ISlackClient> _slackClientMock = new();
    private readonly Mock<ISubmissionChangeNotifier> _notifierMock = new();
    private readonly IOptions<ScoutingSettings> _options;

    public SubmissionWorkflowServiceTests()
    {
        _options = Options.Create(new ScoutingSettings
        {
            SupportedExtensions = new List<string> { ".jpg", ".png", ".pdf", ".docx" },
            SupportedMimeTypePrefixes = new List<string> { "image/", "audio/", "video/" },
            SlackFeedback = new SlackFeedbackConfig
            {
                UnsupportedFile = new FeedbackConfig { Reaction = "warning", Message = "Niet ondersteund: {files}" },
                MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Geen opdrachtnummer" },
                UnknownAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Opdracht {number} bestaat niet" }
            },
            Reviewers = new List<string> { "Sara_Jury" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup", "+1" },
                Rejected = new List<string> { "x", "-1" },
                Awesome = new List<string> { "heart", "star" }
            }
        });

        _assignmentRepoMock.Setup(r => r.GetByCodeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string code, CancellationToken _) => new Assignment { Code = code, StandardPoints = 1, AwesomePoints = 2 });
        _slackClientMock.Setup(c => c.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnknownChannel_ShouldIgnoreMessage()
    {
        // Arrange
        _groupRepoMock.Setup(r => r.GetBySlackChannelIdAsync("UNKNOWN_CHANNEL", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Group?)null);

        var service = CreateService();

        var messageEvent = new SlackEventItemDto
        {
            Channel = "UNKNOWN_CHANNEL",
            Text = "Opdracht 1",
            Files = new List<SlackFileItemDto> { new() { Name = "test.jpg" } }
        };

        // Act
        var result = await service.ProcessSlackMessageAsync(messageEvent);

        // Assert
        result.Success.Should().BeFalse();
        result.Message.Should().Contain("hoort bij geen enkele groep");
        _subRepoMock.Verify(r => r.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_ValidMessage_ShouldSaveFileAndRegisterSubmission()
    {
        // Arrange
        const string channel = "C01TEST";
        var group = new Group { Id = Guid.NewGuid(), Name = "De Zeearenden", Abbreviation = "DZA", SlackChannelId = channel };

        _groupRepoMock.Setup(r => r.GetBySlackChannelIdAsync(channel, It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);

        _parserMock.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns(AssignmentParseResult.Succeeded("4"));

        _slackClientMock.Setup(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));

        var storedName = $"DZA 4 {SlackTimestamp.ToDateTimeOffset("12345.678")!.Value.ToLocalTime():HH-mm-ss}.jpg";
        _storageMock.Setup(s => s.SaveInitialFileAsync(group.Name, storedName, It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/storage/de-zeearenden/DZA 4.jpg");

        var service = CreateService();

        var messageEvent = new SlackEventItemDto
        {
            Channel = channel,
            User = "Lars_Scout",
            Text = "Opdracht 4: Hier is onze foto!",
            Ts = "12345.678",
            Files = new List<SlackFileItemDto>
            {
                new() { Name = "foto.jpg", Size = 1024, Mimetype = "image/jpeg", UrlPrivate = "https://slack.com/files/1" }
            }
        };

        // Act
        var result = await service.ProcessSlackMessageAsync(messageEvent);

        // Assert
        result.Success.Should().BeTrue();
        result.CreatedSubmissionIds.Should().HaveCount(1);
        _subRepoMock.Verify(r => r.AddAsync(It.Is<Submission>(s =>
            s.GroupId == group.Id &&
            s.AssignmentCode == "4" &&
            s.Status == SubmissionStatus.Pending &&
            s.FileName == "foto.jpg" &&
            s.StoragePath == "/storage/de-zeearenden/DZA 4.jpg"
        ), It.IsAny<CancellationToken>()), Times.Once);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Once);
    }

    [Theory]
    [InlineData("thumbsup", SubmissionStatus.Approved)]
    [InlineData("+1", SubmissionStatus.Approved)]
    [InlineData("heart", SubmissionStatus.Awesome)]
    [InlineData("star", SubmissionStatus.Awesome)]
    [InlineData("x", SubmissionStatus.Rejected)]
    [InlineData("-1", SubmissionStatus.Rejected)]
    public async Task ProcessSlackReactionAsync_ConfiguredEmoji_ShouldTransitionStatusAndMoveFile(string reaction, SubmissionStatus expectedStatus)
    {
        // Arrange
        const string channel = "C01TEST";
        const string ts = "12345.678";
        var group = new Group { Id = Guid.NewGuid(), Name = "De Zeearenden", SlackChannelId = channel };
        var submission = new Submission
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Group = group,
            SlackChannelId = channel,
            SlackMessageId = ts,
            FileName = "foto.jpg",
            StoragePath = "/storage/de-zeearenden/foto.jpg",
            Status = SubmissionStatus.Pending
        };

        _subRepoMock.Setup(r => r.GetBySlackMessageIdAsync(channel, ts, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { submission });

        var expectedTargetPath = $"/storage/de-zeearenden/{expectedStatus.ToString().ToLower()}/foto.jpg";
        _storageMock.Setup(s => s.MoveFileToStatusAsync(group.Name, submission.StoragePath, expectedStatus, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTargetPath);

        var service = CreateService();

        var reactionEvent = new SlackEventItemDto
        {
            Type = "reaction_added",
            Reaction = $":{reaction}:",
            User = "Sara_Jury",
            Item = new SlackReactionTargetDto
            {
                Channel = channel,
                Ts = ts
            }
        };

        // Act
        var result = await service.ProcessSlackReactionAsync(reactionEvent);

        // Assert
        result.Success.Should().BeTrue();
        result.NewStatus.Should().Be(expectedStatus);
        submission.Status.Should().Be(expectedStatus);
        submission.StoragePath.Should().Be(expectedTargetPath);
        submission.Reviews.Should().ContainSingle(r => r.Reviewer == "Sara_Jury" && r.Status == expectedStatus);
        _subRepoMock.Verify(r => r.UpdateAsync(submission, It.IsAny<CancellationToken>()), Times.Once);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Once);
    }

    [Theory]
    [InlineData("verslag.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("opname.m4a", "audio/mp4")]
    [InlineData("clip.xyz", "video/x-custom")]
    public async Task ProcessSlackMessageAsync_SupportedMedia_ShouldRegisterWithoutReaction(string fileName, string mimetype)
    {
        // Arrange
        var group = SetupGroupAndStorage();
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, fileName, mimetype));

        // Assert
        result.CreatedSubmissionIds.Should().HaveCount(1);
        _slackClientMock.Verify(c => c.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("script.exe", "application/octet-stream")]
    [InlineData("tekening.svg", "image/svg+xml")]
    [InlineData("data.zip", "application/zip")]
    public async Task ProcessSlackMessageAsync_UnsupportedFile_ShouldSkipAndAddReaction(string fileName, string mimetype)
    {
        // Arrange
        var group = SetupGroupAndStorage();
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, fileName, mimetype));

        // Assert
        result.CreatedSubmissionIds.Should().BeEmpty();
        _subRepoMock.Verify(r => r.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
        _slackClientMock.Verify(c => c.AddReactionAsync(group.SlackChannelId, "12345.678", "warning", It.IsAny<CancellationToken>()), Times.Once);
        _slackClientMock.Verify(c => c.PostThreadReplyAsync(group.SlackChannelId, "12345.678", $"Niet ondersteund: {fileName}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnsupportedFileAlreadyReacted_ShouldNotRepeatReply()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _slackClientMock.Setup(c => c.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = CreateService();

        // Act
        await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "data.zip", "application/zip"));

        // Assert
        _slackClientMock.Verify(c => c.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_FromReviewer_ShouldIgnoreMessage()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        var service = CreateService();
        var message = MessageWithFile(group.SlackChannelId, "foto.jpg", "image/jpeg");
        message.User = "Sara_Jury";

        // Act
        var result = await service.ProcessSlackMessageAsync(message);

        // Assert
        result.Success.Should().BeFalse();
        _subRepoMock.Verify(r => r.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
        _slackClientMock.Verify(c => c.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_MissingAssignmentNumber_ShouldSkipAndGiveFeedback()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _parserMock.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns(AssignmentParseResult.Failed("No assignment number found"));
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "foto.jpg", "image/jpeg"));

        // Assert
        result.Success.Should().BeFalse();
        _slackClientMock.Verify(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _storageMock.Verify(s => s.SaveInitialFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _subRepoMock.Verify(r => r.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Never);
        _slackClientMock.Verify(c => c.AddReactionAsync(group.SlackChannelId, "12345.678", "question", It.IsAny<CancellationToken>()), Times.Once);
        _slackClientMock.Verify(c => c.PostThreadReplyAsync(group.SlackChannelId, "12345.678", "Geen opdrachtnummer", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnknownAssignmentNumber_ShouldSkipAndGiveFeedback()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _parserMock.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns(AssignmentParseResult.Succeeded("999"));
        _assignmentRepoMock.Setup(r => r.GetByCodeAsync("999", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Assignment?)null);
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "foto.jpg", "image/jpeg"));

        // Assert
        result.Success.Should().BeFalse();
        _slackClientMock.Verify(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _storageMock.Verify(s => s.SaveInitialFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()), Times.Never);
        _subRepoMock.Verify(r => r.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Never);
        _slackClientMock.Verify(c => c.AddReactionAsync(group.SlackChannelId, "12345.678", "question", It.IsAny<CancellationToken>()), Times.Once);
        _slackClientMock.Verify(c => c.PostThreadReplyAsync(group.SlackChannelId, "12345.678", "Opdracht 999 bestaat niet", It.IsAny<CancellationToken>()), Times.Once);
    }

    private Group SetupGroupAndStorage()
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "De Zeearenden", SlackChannelId = "C01TEST" };
        _groupRepoMock.Setup(r => r.GetBySlackChannelIdAsync(group.SlackChannelId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(group);
        _parserMock.Setup(p => p.Parse(It.IsAny<string>()))
            .Returns(AssignmentParseResult.Succeeded("4"));
        _slackClientMock.Setup(c => c.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MemoryStream(new byte[] { 1, 2, 3 }));
        _storageMock.Setup(s => s.SaveInitialFileAsync(group.Name, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/storage/de-zeearenden/file");
        return group;
    }

    private static SlackEventItemDto MessageWithFile(string channel, string fileName, string mimetype) => new()
    {
        Channel = channel,
        User = "Lars_Scout",
        Text = "Opdracht 4",
        Ts = "12345.678",
        Files = new List<SlackFileItemDto>
        {
            new() { Name = fileName, Size = 1024, Mimetype = mimetype, UrlPrivate = "https://slack.com/files/1" }
        }
    };

    [Theory]
    [InlineData("Random_Scout")]
    [InlineData(null)]
    public async Task ProcessSlackReactionAsync_NonReviewer_ShouldIgnoreReaction(string? user)
    {
        // Arrange
        var service = CreateService();

        var reactionEvent = new SlackEventItemDto
        {
            Type = "reaction_added",
            Reaction = ":heart:",
            User = user,
            Item = new SlackReactionTargetDto
            {
                Channel = "C01ZEEARENDEN",
                Ts = "1720000000.0001"
            }
        };

        // Act
        var result = await service.ProcessSlackReactionAsync(reactionEvent);

        // Assert
        result.Success.Should().BeFalse();
        _storageMock.Verify(s => s.MoveFileToStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SubmissionStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        _subRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Never);
    }

    [Fact]
    public async Task OverrideStatusAsync_ShouldMoveFileAndRecordReasonInTimeline()
    {
        // Arrange
        var group = new Group { Id = Guid.NewGuid(), Name = "De Wolven" };
        var submission = new Submission { GroupId = group.Id, Group = group, StoragePath = "/storage/de-wolven/CDW 3.jpg", SlackChannelId = "C1", SlackMessageId = "1.1" };
        _subRepoMock.Setup(r => r.GetByIdAsync(submission.Id, It.IsAny<CancellationToken>())).ReturnsAsync(submission);
        _subRepoMock.Setup(r => r.GetBySlackMessageIdAsync("C1", "1.1", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { submission });
        _storageMock.Setup(s => s.MoveFileToStatusAsync(group.Name, submission.StoragePath, SubmissionStatus.Approved, It.IsAny<CancellationToken>()))
            .ReturnsAsync("/storage/de-wolven/approved/CDW 3.jpg");
        var service = CreateService();

        // Act
        await service.OverrideStatusAsync(submission.Id, SubmissionStatus.Approved, "Reaction was placed on the wrong message");

        // Assert
        submission.Status.Should().Be(SubmissionStatus.Approved);
        submission.StoragePath.Should().Be("/storage/de-wolven/approved/CDW 3.jpg");
        submission.Reviews.Should().ContainSingle(r =>
            r.Reviewer == SubmissionWorkflowService.DashboardOverrideReviewer &&
            r.Notes == "Reaction was placed on the wrong message");
        _subRepoMock.Verify(r => r.UpdateAsync(submission, It.IsAny<CancellationToken>()), Times.Once);
        _notifierMock.Verify(n => n.NotifyChanged(), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OverrideStatusAsync_WithoutReason_ShouldThrow(string reason)
    {
        var service = CreateService();

        var act = () => service.OverrideStatusAsync(Guid.NewGuid(), SubmissionStatus.Approved, reason);

        await act.Should().ThrowAsync<ArgumentException>();
        _subRepoMock.Verify(r => r.UpdateAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private SubmissionWorkflowService CreateService()
    {
        return new SubmissionWorkflowService(
            _subRepoMock.Object,
            _groupRepoMock.Object,
            _assignmentRepoMock.Object,
            _parserMock.Object,
            _storageMock.Object,
            _slackClientMock.Object,
            _notifierMock.Object,
            TestRuntime.For(_options),
            _options,
            NullLogger<SubmissionWorkflowService>.Instance
        );
    }
}
