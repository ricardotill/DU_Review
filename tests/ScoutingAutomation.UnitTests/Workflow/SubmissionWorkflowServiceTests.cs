using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
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
    private readonly ISubmissionRepository _subRepoMock = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groupRepoMock = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignmentRepoMock = Substitute.For<IAssignmentRepository>();
    private readonly IAssignmentParser _parserMock = Substitute.For<IAssignmentParser>();
    private readonly IFileStorageService _storageMock = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slackClientMock = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifierMock = Substitute.For<ISubmissionChangeNotifier>();
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

        _assignmentRepoMock.GetByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new Assignment { Code = ci.ArgAt<string>(0), StandardPoints = 1, AwesomePoints = 2 });
        _slackClientMock.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnknownChannel_ShouldIgnoreMessage()
    {
        // Arrange
        _groupRepoMock.GetBySlackChannelIdAsync("UNKNOWN_CHANNEL", Arg.Any<CancellationToken>())
            .Returns((Group?)null);

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
        await _subRepoMock.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_ValidMessage_ShouldSaveFileAndRegisterSubmission()
    {
        // Arrange
        const string channel = "C01TEST";
        var group = new Group { Id = Guid.NewGuid(), Name = "De Zeearenden", Abbreviation = "DZA", SlackChannelId = channel };

        _groupRepoMock.GetBySlackChannelIdAsync(channel, Arg.Any<CancellationToken>())
            .Returns(group);

        _parserMock.Parse(Arg.Any<string>())
            .Returns(AssignmentParseResult.Succeeded("4"));

        _slackClientMock.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));

        var storedName = $"DZA 4 {SlackTimestamp.ToDateTimeOffset("12345.678")!.Value.ToLocalTime():HH-mm-ss}.jpg";
        _storageMock.SaveInitialFileAsync(group.Name, storedName, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns("/storage/de-zeearenden/DZA 4.jpg");

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
        await _subRepoMock.Received(1).AddAsync(Arg.Is<Submission>(s =>
            s.GroupId == group.Id &&
            s.AssignmentCode == "4" &&
            s.Status == SubmissionStatus.Pending &&
            s.FileName == "foto.jpg" &&
            s.StoragePath == "/storage/de-zeearenden/DZA 4.jpg"
        ), Arg.Any<CancellationToken>());
        _notifierMock.Received(1).NotifyChanged();
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

        _subRepoMock.GetBySlackMessageIdAsync(channel, ts, Arg.Any<CancellationToken>())
            .Returns(new[] { submission });

        var expectedTargetPath = $"/storage/de-zeearenden/{expectedStatus.ToString().ToLower()}/foto.jpg";
        _storageMock.MoveFileToStatusAsync(group.Name, submission.StoragePath, expectedStatus, Arg.Any<CancellationToken>())
            .Returns(expectedTargetPath);

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
        await _subRepoMock.Received(1).UpdateAsync(submission, Arg.Any<CancellationToken>());
        _notifierMock.Received(1).NotifyChanged();
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
        await _slackClientMock.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _subRepoMock.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        await _slackClientMock.Received(1).AddReactionAsync(group.SlackChannelId, "12345.678", "warning", Arg.Any<CancellationToken>());
        await _slackClientMock.Received(1).PostThreadReplyAsync(group.SlackChannelId, "12345.678", $"Niet ondersteund: {fileName}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnsupportedFileAlreadyReacted_ShouldNotRepeatReply()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _slackClientMock.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var service = CreateService();

        // Act
        await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "data.zip", "application/zip"));

        // Assert
        await _slackClientMock.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _subRepoMock.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        await _slackClientMock.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_MissingAssignmentNumber_ShouldSkipAndGiveFeedback()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _parserMock.Parse(Arg.Any<string>())
            .Returns(AssignmentParseResult.Failed("No assignment number found"));
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "foto.jpg", "image/jpeg"));

        // Assert
        result.Success.Should().BeFalse();
        await _slackClientMock.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _storageMock.DidNotReceive().SaveInitialFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _subRepoMock.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        _notifierMock.DidNotReceive().NotifyChanged();
        await _slackClientMock.Received(1).AddReactionAsync(group.SlackChannelId, "12345.678", "question", Arg.Any<CancellationToken>());
        await _slackClientMock.Received(1).PostThreadReplyAsync(group.SlackChannelId, "12345.678", "Geen opdrachtnummer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessSlackMessageAsync_UnknownAssignmentNumber_ShouldSkipAndGiveFeedback()
    {
        // Arrange
        var group = SetupGroupAndStorage();
        _parserMock.Parse(Arg.Any<string>())
            .Returns(AssignmentParseResult.Succeeded("999"));
        _assignmentRepoMock.GetByCodeAsync("999", Arg.Any<CancellationToken>())
            .Returns((Assignment?)null);
        var service = CreateService();

        // Act
        var result = await service.ProcessSlackMessageAsync(MessageWithFile(group.SlackChannelId, "foto.jpg", "image/jpeg"));

        // Assert
        result.Success.Should().BeFalse();
        await _slackClientMock.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _storageMock.DidNotReceive().SaveInitialFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _subRepoMock.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        _notifierMock.DidNotReceive().NotifyChanged();
        await _slackClientMock.Received(1).AddReactionAsync(group.SlackChannelId, "12345.678", "question", Arg.Any<CancellationToken>());
        await _slackClientMock.Received(1).PostThreadReplyAsync(group.SlackChannelId, "12345.678", "Opdracht 999 bestaat niet", Arg.Any<CancellationToken>());
    }

    private Group SetupGroupAndStorage()
    {
        var group = new Group { Id = Guid.NewGuid(), Name = "De Zeearenden", SlackChannelId = "C01TEST" };
        _groupRepoMock.GetBySlackChannelIdAsync(group.SlackChannelId, Arg.Any<CancellationToken>())
            .Returns(group);
        _parserMock.Parse(Arg.Any<string>())
            .Returns(AssignmentParseResult.Succeeded("4"));
        _slackClientMock.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryStream(new byte[] { 1, 2, 3 }));
        _storageMock.SaveInitialFileAsync(group.Name, Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns("/storage/de-zeearenden/file");
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
        await _storageMock.DidNotReceive().MoveFileToStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SubmissionStatus>(), Arg.Any<CancellationToken>());
        await _subRepoMock.DidNotReceive().UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        _notifierMock.DidNotReceive().NotifyChanged();
    }

    [Fact]
    public async Task OverrideStatusAsync_ShouldMoveFileAndRecordReasonInTimeline()
    {
        // Arrange
        var group = new Group { Id = Guid.NewGuid(), Name = "De Wolven" };
        var submission = new Submission { GroupId = group.Id, Group = group, StoragePath = "/storage/de-wolven/CDW 3.jpg", SlackChannelId = "C1", SlackMessageId = "1.1" };
        _subRepoMock.GetByIdAsync(submission.Id, Arg.Any<CancellationToken>()).Returns(submission);
        _subRepoMock.GetBySlackMessageIdAsync("C1", "1.1", Arg.Any<CancellationToken>()).Returns(new[] { submission });
        _storageMock.MoveFileToStatusAsync(group.Name, submission.StoragePath, SubmissionStatus.Approved, Arg.Any<CancellationToken>())
            .Returns("/storage/de-wolven/approved/CDW 3.jpg");
        var service = CreateService();

        // Act
        await service.OverrideStatusAsync(submission.Id, SubmissionStatus.Approved, "Reaction was placed on the wrong message");

        // Assert
        submission.Status.Should().Be(SubmissionStatus.Approved);
        submission.StoragePath.Should().Be("/storage/de-wolven/approved/CDW 3.jpg");
        submission.Reviews.Should().ContainSingle(r =>
            r.Reviewer == SubmissionWorkflowService.DashboardOverrideReviewer &&
            r.Notes == "Reaction was placed on the wrong message");
        await _subRepoMock.Received(1).UpdateAsync(submission, Arg.Any<CancellationToken>());
        _notifierMock.Received(1).NotifyChanged();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task OverrideStatusAsync_WithoutReason_ShouldThrow(string reason)
    {
        var service = CreateService();

        var act = () => service.OverrideStatusAsync(Guid.NewGuid(), SubmissionStatus.Approved, reason);

        await act.Should().ThrowAsync<ArgumentException>();
        await _subRepoMock.DidNotReceive().UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
    }

    private SubmissionWorkflowService CreateService()
    {
        return new SubmissionWorkflowService(
            _subRepoMock,
            _groupRepoMock,
            _assignmentRepoMock,
            _parserMock,
            _storageMock,
            _slackClientMock,
            _notifierMock,
            TestRuntime.For(_options),
            _options,
            NullLogger<SubmissionWorkflowService>.Instance
        );
    }
}
