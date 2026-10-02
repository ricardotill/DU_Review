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

/// <summary>
/// Branches of the Slack workflow that the main workflow tests do not reach.
/// </summary>
public class WorkflowEdgeCaseTests
{
    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<IAssignmentRepository> _assignments = new();
    private readonly Mock<IAssignmentParser> _parser = new();
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Mock<ISubmissionChangeNotifier> _notifier = new();
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };

    public WorkflowEdgeCaseTests()
    {
        // Like the real repository: no submissions is an empty list, never null
        _submissions.Setup(s => s.GetBySlackMessageIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<Submission>());
    }

    private SubmissionWorkflowService Create(ScoutingSettings? settings = null)
    {
        var options = Options.Create(settings ?? new ScoutingSettings
        {
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig { Approved = new List<string> { "thumbsup" } }
        });
        return new(_submissions.Object, _groups.Object, _assignments.Object, _parser.Object, _storage.Object, _slack.Object, _notifier.Object,
            TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Message_WithoutChannel_ShouldBeIgnored(string? channel)
    {
        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto { Channel = channel });

        result.Success.Should().BeFalse();
        _groups.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Message_WithoutFiles_ShouldBeIgnoredWithoutFeedback()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);

        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto { Channel = "C1", User = "U_SCOUT", Text = "Opdracht 1", Files = new List<SlackFileItemDto>() });

        result.Message.Should().Contain("Geen bestanden");
        _parser.VerifyNoOtherCalls();
        _slack.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Message_AlreadyRegisteredOrNamelessFile_ShouldBeSkipped()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Succeeded(1));
        _assignments.Setup(a => a.GetByNumberAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 1 });
        _submissions.Setup(s => s.ExistsAsync("C1", "1.0", It.IsAny<string?>(), "foto.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U_SCOUT", Text = "Opdracht 1", Ts = "1.0",
            Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" }, new() { Name = " " } }
        });

        result.CreatedSubmissionIds.Should().BeEmpty();
        _slack.Verify(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _notifier.Verify(n => n.NotifyChanged(), Times.Never);
    }

    [Fact]
    public async Task Message_WithoutTimestampOrMetadata_ShouldUseFallbacks()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Succeeded(1));
        _assignments.Setup(a => a.GetByNumberAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 1 });
        _slack.Setup(s => s.DownloadFileAsync("sim://foto.jpg", "foto.jpg", It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream(new byte[] { 1 }));
        using var temp = new TempDirectory();
        var saved = Path.Combine(temp.Path, "DWO 1.jpg");
        await File.WriteAllBytesAsync(saved, new byte[] { 1, 2, 3, 4 });
        _storage.Setup(s => s.SaveInitialFileAsync(_group.Name, It.IsRegex(@"^DWO 1 \d{2}-\d{2}-\d{2}\.jpg$"), It.IsAny<Stream>(), It.IsAny<CancellationToken>())).ReturnsAsync(saved);
        Submission? added = null;
        _submissions.Setup(s => s.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>())).Callback<Submission, CancellationToken>((s, _) => added = s);

        // Reviewer check compares user IDs, so a message without user is from a scout
        await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", Text = "Opdracht 1",
            Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        added.Should().NotBeNull();
        added!.Submitter.Should().Be("Unknown");
        added.SlackMessageId.Should().NotBeNullOrWhiteSpace();
        added.FileSizeBytes.Should().Be(4);
        added.ContentType.Should().Be("application/octet-stream");
    }

    [Theory]
    [InlineData("Tim de Vries", "Tim de Vries")]
    [InlineData(null, null)]
    public async Task Message_ShouldStoreTheSubmitterNameFetchedOncePerMessage(string? slackName, string? expected)
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Succeeded(1));
        _assignments.Setup(a => a.GetByNumberAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 1 });
        _slack.Setup(s => s.IsConnected).Returns(true);
        _slack.Setup(s => s.GetUserNameAsync("U_SCOUT", It.IsAny<CancellationToken>()))
            .ReturnsAsync(slackName == null ? SlackUserLookup.Failed("user_not_found") : SlackUserLookup.Found(slackName));
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1 }));
        _storage.Setup(s => s.SaveInitialFileAsync(_group.Name, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("DWO 1.jpg");
        var added = new List<Submission>();
        _submissions.Setup(s => s.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>())).Callback<Submission, CancellationToken>((s, _) => added.Add(s));

        await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U_SCOUT", Text = "Opdracht 1", Ts = "1.0",
            Files = new List<SlackFileItemDto>
            {
                new() { Name = "a.jpg", Size = 1, UrlPrivate = "https://files.slack.com/a.jpg" },
                new() { Name = "b.jpg", Size = 1, UrlPrivate = "https://files.slack.com/b.jpg" }
            }
        });

        added.Should().HaveCount(2);
        added.Should().AllSatisfy(s =>
        {
            s.Submitter.Should().Be("U_SCOUT");
            s.SubmitterName.Should().Be(expected);
            s.SubmitterDisplayName.Should().Be(expected ?? "U_SCOUT");
        });
        _slack.Verify(s => s.GetUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Message_WithEmptySettingsLists_ShouldTreatEveryFileAsUnsupported()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Succeeded(1));
        _assignments.Setup(a => a.GetByNumberAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 1 });
        var settings = new ScoutingSettings { SupportedExtensions = null!, SupportedMimeTypePrefixes = null!, Reviewers = null!, SlackFeedback = null! };

        var result = await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "Opdracht 1", Ts = "1.0",
            Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg", Mimetype = "image/jpeg" } }
        });

        result.CreatedSubmissionIds.Should().BeEmpty();
        // Default feedback config still applies when the section is missing
        _slack.Verify(s => s.AddReactionAsync("C1", "1.0", "warning", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Message_FeedbackWithoutTimestamp_ShouldNotPostAnything()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Failed("none"));

        await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        _slack.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Message_FeedbackWithReactionOnly_ShouldNotPostReply()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Failed("none"));
        _slack.Setup(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var settings = new ScoutingSettings
        {
            SlackFeedback = new SlackFeedbackConfig { MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "" } }
        };

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        _slack.Verify(s => s.AddReactionAsync("C1", "1.0", "question", It.IsAny<CancellationToken>()), Times.Once);
        _slack.Verify(s => s.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Message_FeedbackDisabled_ShouldNotContactSlack()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Failed("none"));
        var settings = new ScoutingSettings { SlackFeedback = new SlackFeedbackConfig { MissingAssignmentNumber = null! } };

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        _slack.VerifyNoOtherCalls();
    }

    public static IEnumerable<object[]> IgnoredReactions() => new[]
    {
        new object[] { new SlackEventItemDto { User = "U_JURY", Reaction = "::" }, false },
        new object[] { new SlackEventItemDto { User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Ts = "1.0" } }, false },
        new object[] { new SlackEventItemDto { User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1" } }, false },
        new object[] { new SlackEventItemDto { User = "U_JURY", Reaction = "tada", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" } }, false },
        new object[] { new SlackEventItemDto { User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "404" } }, true }
    };

    [Theory]
    [MemberData(nameof(IgnoredReactions))]
    public async Task Reaction_ThatCannotBeApplied_ShouldNotChangeAnything(SlackEventItemDto reaction, bool looksUpSubmission)
    {
        var result = await Create().ProcessSlackReactionAsync(reaction);

        result.Success.Should().BeFalse();
        _submissions.Verify(s => s.GetBySlackMessageIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), looksUpSubmission ? Times.Once() : Times.Never());
        _storage.VerifyNoOtherCalls();
        _notifier.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Reaction_WithoutReactionConfig_ShouldBeIgnored()
    {
        var settings = new ScoutingSettings { Reviewers = new List<string> { "U_JURY" }, ReviewStatusReactions = null! };

        var result = await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });

        result.Message.Should().Contain("is geen beoordeling");
    }

    [Fact]
    public async Task Reaction_SubmissionWithoutLoadedGroup_ShouldLookUpGroup()
    {
        var submission = new Submission { GroupId = _group.Id, StoragePath = "/s/f.jpg" };
        _submissions.Setup(s => s.GetBySlackMessageIdAsync("C1", "1.0", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { submission });
        _groups.Setup(g => g.GetByIdAsync(_group.Id, It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _storage.Setup(s => s.MoveFileToStatusAsync(_group.Name, "/s/f.jpg", SubmissionStatus.Approved, It.IsAny<CancellationToken>())).ReturnsAsync("/s/approved/f.jpg");

        var result = await Create().ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = ":THUMBSUP:", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });

        result.Success.Should().BeTrue();
        submission.StoragePath.Should().Be("/s/approved/f.jpg");
    }

    [Fact]
    public async Task Override_UnknownSubmission_ShouldThrow()
    {
        var act = () => Create().OverrideStatusAsync(Guid.NewGuid(), SubmissionStatus.Approved, "reden");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Override_GroupMissing_ShouldUseFallbackFolder()
    {
        var submission = new Submission { GroupId = Guid.NewGuid(), StoragePath = "/s/f.jpg" };
        _submissions.Setup(s => s.GetByIdAsync(submission.Id, It.IsAny<CancellationToken>())).ReturnsAsync(submission);
        _storage.Setup(s => s.MoveFileToStatusAsync("unknown-group", "/s/f.jpg", SubmissionStatus.Rejected, It.IsAny<CancellationToken>())).ReturnsAsync("/s/rejected/f.jpg");

        var result = await Create().OverrideStatusAsync(submission.Id, SubmissionStatus.Rejected, "  dubbel  ");

        result.Reviews.Single().Notes.Should().Be("dubbel");
    }
}
