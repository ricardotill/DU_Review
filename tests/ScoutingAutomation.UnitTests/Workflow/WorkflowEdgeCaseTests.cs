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

/// <summary>
/// Branches of the Slack workflow that the main workflow tests do not reach.
/// </summary>
public class WorkflowEdgeCaseTests
{
    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IAssignmentParser _parser = Substitute.For<IAssignmentParser>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };

    public WorkflowEdgeCaseTests()
    {
        // Like the real repository: no submissions is an empty list, never null
        _submissions.GetBySlackMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Submission>());
    }

    private SubmissionWorkflowService Create(ScoutingSettings? settings = null)
    {
        var options = Options.Create(settings ?? new ScoutingSettings
        {
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig { Approved = new List<string> { "thumbsup" } }
        });
        return new(_submissions, _groups, _assignments, _parser, _storage, _slack, _notifier,
            TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public async Task Message_WithoutChannel_ShouldBeIgnored(string? channel)
    {
        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto { Channel = channel });

        result.Success.Should().BeFalse();
        _groups.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Message_WithoutFiles_ShouldBeIgnoredWithoutFeedback()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);

        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto { Channel = "C1", User = "U_SCOUT", Text = "Opdracht 1", Files = new List<SlackFileItemDto>() });

        result.Message.Should().Contain("Geen bestanden");
        _parser.ReceivedCalls().Should().BeEmpty();
        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Message_AlreadyRegisteredOrNamelessFile_ShouldBeSkipped()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Succeeded("1"));
        _assignments.GetByCodeAsync("1", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "1" });
        _submissions.ExistsAsync("C1", "1.0", Arg.Any<string?>(), "foto.jpg", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U_SCOUT", Text = "Opdracht 1", Ts = "1.0",
            Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" }, new() { Name = " " } }
        });

        result.CreatedSubmissionIds.Should().BeEmpty();
        await _slack.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _notifier.DidNotReceive().NotifyChanged();
    }

    [Fact]
    public async Task Message_WithoutTimestampOrMetadata_ShouldUseFallbacks()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Succeeded("1"));
        _assignments.GetByCodeAsync("1", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "1" });
        _slack.DownloadFileAsync("sim://foto.jpg", "foto.jpg", Arg.Any<CancellationToken>()).Returns(new MemoryStream(new byte[] { 1 }));
        using var temp = new TempDirectory();
        var saved = Path.Combine(temp.Path, "DWO 1.jpg");
        await File.WriteAllBytesAsync(saved, new byte[] { 1, 2, 3, 4 });
        _storage.SaveInitialFileAsync(_group.Name, Arg.Is<string>(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^DWO 1 \d{2}-\d{2}-\d{2}\.jpg$")), Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(saved);
        Submission? added = null;
        _submissions.When(s => s.AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>())).Do(ci => added = ci.ArgAt<Submission>(0));

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
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Succeeded("1"));
        _assignments.GetByCodeAsync("1", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "1" });
        _slack.IsConnected.Returns(true);
        _slack.GetUserNameAsync("U_SCOUT", Arg.Any<CancellationToken>())
            .Returns(slackName == null ? SlackUserLookup.Failed("user_not_found") : SlackUserLookup.Found(slackName));
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(new byte[] { 1 }));
        _storage.SaveInitialFileAsync(_group.Name, Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns("DWO 1.jpg");
        var added = new List<Submission>();
        _submissions.When(s => s.AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>())).Do(ci => added.Add(ci.ArgAt<Submission>(0)));

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
        await _slack.Received(1).GetUserNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_WithEmptySettingsLists_ShouldTreatEveryFileAsUnsupported()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Succeeded("1"));
        _assignments.GetByCodeAsync("1", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "1" });
        var settings = new ScoutingSettings { SupportedExtensions = null!, SupportedMimeTypePrefixes = null!, Reviewers = null!, SlackFeedback = null! };

        var result = await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "Opdracht 1", Ts = "1.0",
            Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg", Mimetype = "image/jpeg" } }
        });

        result.CreatedSubmissionIds.Should().BeEmpty();
        // Default feedback config still applies when the section is missing
        await _slack.Received(1).AddReactionAsync("C1", "1.0", "warning", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_FeedbackWithoutTimestamp_ShouldNotPostAnything()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Failed("none"));

        await Create().ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Message_FeedbackWithReactionOnly_ShouldNotPostReply()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Failed("none"));
        _slack.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var settings = new ScoutingSettings
        {
            SlackFeedback = new SlackFeedbackConfig { MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "" } }
        };

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        await _slack.Received(1).AddReactionAsync("C1", "1.0", "question", Arg.Any<CancellationToken>());
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_FeedbackDisabled_ShouldNotContactSlack()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Failed("none"));
        var settings = new ScoutingSettings { SlackFeedback = new SlackFeedbackConfig { MissingAssignmentNumber = null! } };

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Text = "hallo", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = "foto.jpg" } }
        });

        _slack.ReceivedCalls().Should().BeEmpty();
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
        await _submissions.Received(looksUpSubmission ? 1 : 0).GetBySlackMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _storage.ReceivedCalls().Should().BeEmpty();
        _notifier.ReceivedCalls().Should().BeEmpty();
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
        _submissions.GetBySlackMessageIdAsync("C1", "1.0", Arg.Any<CancellationToken>()).Returns(new[] { submission });
        _groups.GetByIdAsync(_group.Id, Arg.Any<CancellationToken>()).Returns(_group);
        _storage.MoveFileToStatusAsync(_group.Name, "/s/f.jpg", SubmissionStatus.Approved, Arg.Any<CancellationToken>()).Returns("/s/approved/f.jpg");

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
        _submissions.GetByIdAsync(submission.Id, Arg.Any<CancellationToken>()).Returns(submission);
        _storage.MoveFileToStatusAsync("unknown-group", "/s/f.jpg", SubmissionStatus.Rejected, Arg.Any<CancellationToken>()).Returns("/s/rejected/f.jpg");

        var result = await Create().OverrideStatusAsync(submission.Id, SubmissionStatus.Rejected, "  dubbel  ");

        result.Reviews.Single().Notes.Should().Be("dubbel");
    }
}
