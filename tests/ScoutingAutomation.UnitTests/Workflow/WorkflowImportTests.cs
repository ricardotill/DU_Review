using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Importing history, message times, failed downloads and reviews of messages with several files.
/// </summary>
public class WorkflowImportTests
{
    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly List<Submission> _added = new();

    public WorkflowImportTests()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _assignments.GetByCodeAsync("4", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "4" });
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[] { 1 }));
        _slack.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _storage.SaveInitialFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/s/{ci.ArgAt<string>(1)}");
        _storage.MoveFileToStatusAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<SubmissionStatus>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var path = ci.ArgAt<string>(1); var status = ci.ArgAt<SubmissionStatus>(2); return $"/s/{status}/{Path.GetFileName(path)}"; });
        _submissions.When(s => s.AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>())).Do(ci => _added.Add(ci.ArgAt<Submission>(0)));
        _submissions.GetBySlackMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<Submission>());
    }

    private static readonly IOptions<ScoutingSettings> Settings = Options.Create(new ScoutingSettings
    {
        SupportedExtensions = new List<string> { ".jpg" },
        Reviewers = new List<string> { "U_JURY", "U_JURY2" },
        ReviewStatusReactions = new ReviewStatusReactionsConfig
        {
            Approved = new List<string> { "thumbsup" },
            Rejected = new List<string> { "x" },
            Awesome = new List<string> { "heart" }
        }
    });

    private readonly RuntimeSettingsService _runtime = TestRuntime.For(Settings);

    private SubmissionWorkflowService Create() => new(
        _submissions, _groups, _assignments, new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        _storage, _slack, _notifier, _runtime, Settings,
        NullLogger<SubmissionWorkflowService>.Instance);

    private static SlackEventItemDto Message(string text, params string[] files) => new()
    {
        Channel = "C1", User = "U_SCOUT", Text = text, Ts = "1727704800.500000",
        Files = files.Select(f => new SlackFileItemDto { Name = f, Size = 1, UrlPrivate = $"https://files.slack.com/{f}" }).ToList()
    };

    [Theory]
    [InlineData("hallo", "a.jpg")]         // no number
    [InlineData("Opdracht 9", "a.jpg")]    // unknown assignment
    [InlineData("Opdracht 4", "a.zip")]    // unsupported file
    public async Task Import_ShouldNeverPostToSlack(string text, string file)
    {
        _assignments.GetByCodeAsync("9", Arg.Any<CancellationToken>()).Returns((Assignment?)null);

        await Create().ImportSlackMessageAsync(Message(text, file));

        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_ShouldBeStoredWithItsSlackTime()
    {
        await Create().ImportSlackMessageAsync(Message("Opdracht 4", "a.jpg"));

        _added.Single().CreatedAt.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1727704800500));
    }

    [Fact]
    public async Task FailedDownload_Live_ShouldSkipFileReportItAndAskForRepost()
    {
        _slack.DownloadFileAsync("https://files.slack.com/weg.jpg", "weg.jpg", Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("404"));

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4", "weg.jpg", "goed.jpg"));

        result.FailedDownloads.Should().Equal("weg.jpg");
        result.CreatedSubmissionIds.Should().ContainSingle();
        _added.Single().FileName.Should().Be("goed.jpg");
        await _slack.Received(1).AddReactionAsync("C1", "1727704800.500000", "warning", Arg.Any<CancellationToken>());
        await _slack.Received(1).PostThreadReplyAsync("C1", "1727704800.500000", Arg.Is<string>(t => t.Contains("weg.jpg")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedDownload_Import_ShouldOnlyReport()
    {
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("404"));

        var result = await Create().ImportSlackMessageAsync(Message("Opdracht 4", "weg.jpg"));

        result.FailedDownloads.Should().Equal("weg.jpg");
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelledDownload_ShouldStopProcessing()
    {
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new OperationCanceledException());

        var act = () => Create().ProcessSlackMessageAsync(Message("Opdracht 4", "a.jpg"), stopping.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LiveReaction_OnMessageWithSeveralFiles_ShouldReviewAllOfThem()
    {
        var files = new[]
        {
            new Submission { GroupId = _group.Id, Group = _group, StoragePath = "/s/DWO 4.jpg" },
            new Submission { GroupId = _group.Id, Group = _group, StoragePath = "/s/DWO 4 (2).jpg" }
        };
        _submissions.GetBySlackMessageIdAsync("C1", "1.0", Arg.Any<CancellationToken>()).Returns(files);

        var result = await Create().ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });

        result.SubmissionId.Should().Be(files[0].Id);
        files.Should().OnlyContain(f => f.Status == SubmissionStatus.Approved && f.StoragePath.StartsWith("/s/Approved/"));
        await _submissions.Received(2).UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        _notifier.Received(1).NotifyChanged();
    }

    private static SlackMessageReactionDto Reaction(string name, params string[] users) => new() { Name = name, Users = users.ToList() };

    public static IEnumerable<object?[]> ImportedReactions() => new[]
    {
        new object?[] { null, null, false, null },
        new object?[] { new[] { Reaction("thumbsup", "U_SCOUT") }, null, false, null },
        new object?[] { new[] { Reaction("tada", "U_JURY") }, null, false, null },
        new object?[] { new[] { new SlackMessageReactionDto { Name = "thumbsup", Users = null } }, null, false, null },
        new object?[] { new[] { Reaction("thumbsup", "U_JURY", "U_JURY2") }, SubmissionStatus.Approved, false, "U_JURY" },
        new object?[] { new[] { Reaction("x", "U_JURY2"), Reaction("thumbsup", "U_JURY") }, SubmissionStatus.Approved, true, "U_JURY" },
        new object?[] { new[] { Reaction("thumbsup", "U_JURY"), Reaction(":heart:", "U_SCOUT", "U_JURY2") }, SubmissionStatus.Awesome, true, "U_JURY2" },
        new object?[] { new[] { Reaction("x", "U_JURY") }, SubmissionStatus.Rejected, false, "U_JURY" }
    };

    [Theory]
    [MemberData(nameof(ImportedReactions))]
    public async Task ImportedReactions_BestReviewerStatusShouldWin(SlackMessageReactionDto[]? reactions, SubmissionStatus? expected, bool conflict, string? reviewer)
    {
        var submission = new Submission { GroupId = _group.Id, Group = _group, StoragePath = "/s/DWO 4.jpg" };
        _submissions.GetBySlackMessageIdAsync("C1", "1.0", Arg.Any<CancellationToken>()).Returns(new[] { submission });

        var result = await Create().ApplyImportedReactionsAsync("C1", "1.0", reactions);

        result.Status.Should().Be(expected);
        result.Conflict.Should().Be(conflict);
        if (expected.HasValue)
        {
            submission.Status.Should().Be(expected.Value);
            submission.Reviews.Single().Reviewer.Should().Be(reviewer);
            submission.Reviews.Single().ReactionEmoji.Should().NotContain(":");
        }
        else
        {
            submission.Reviews.Should().BeEmpty();
        }
    }

    private static SlackEventItemDto Removed(string reaction, string user = "U_JURY", string? channel = "C1", string? ts = "1.0") => new()
    {
        Type = "reaction_removed", User = user, Reaction = reaction,
        Item = channel == null ? null : new SlackReactionTargetDto { Channel = channel, Ts = ts }
    };

    private Submission ApprovedSubmission()
    {
        var submission = new Submission { GroupId = _group.Id, Group = _group, StoragePath = "/s/Approved/DWO 4.jpg" };
        submission.TransitionStatus(SubmissionStatus.Approved, submission.StoragePath, "U_JURY", "thumbsup");
        _submissions.GetBySlackMessageIdAsync("C1", "1.0", Arg.Any<CancellationToken>()).Returns(new[] { submission });
        return submission;
    }

    [Theory]
    [InlineData("thumbsup", "U_SCOUT")] // not a reviewer
    [InlineData("tada", "U_JURY")]      // not a review emoji
    [InlineData("", "U_JURY")]
    public async Task RemovedReaction_ThatIsNoReview_ShouldBeIgnored(string reaction, string user)
    {
        var result = await Create().ProcessSlackReactionRemovedAsync(Removed(reaction, user));

        result.Success.Should().BeFalse();
        await _slack.DidNotReceive().GetReactionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("C1", null)]
    public async Task RemovedReaction_WithoutTarget_ShouldFail(string? channel, string? ts)
    {
        var result = await Create().ProcessSlackReactionRemovedAsync(Removed("thumbsup", channel: channel, ts: ts));

        result.Message.Should().Contain("zonder kanaal of tijdstempel");
    }

    [Fact]
    public async Task RemovedReaction_OnMessageWithoutSubmission_ShouldBeIgnored()
    {
        var result = await Create().ProcessSlackReactionRemovedAsync(Removed("thumbsup"));

        result.Message.Should().StartWith("Geen inzending");
        await _slack.DidNotReceive().GetReactionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovedReaction_WhenSlackCannotBeRead_ShouldLeaveStatus()
    {
        var submission = ApprovedSubmission();
        _slack.GetReactionsAsync("C1", "1.0", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SlackMessageReactionDto>?)null);

        var result = await Create().ProcessSlackReactionRemovedAsync(Removed("thumbsup"));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("status ongewijzigd");
        submission.Status.Should().Be(SubmissionStatus.Approved);
        await _submissions.DidNotReceive().UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemovedReaction_LastReview_ShouldMakeSubmissionPendingAgain()
    {
        var submission = ApprovedSubmission();
        _slack.GetReactionsAsync("C1", "1.0", Arg.Any<CancellationToken>())
            .Returns(new[] { Reaction("thumbsup", "U_SCOUT"), Reaction("tada", "U_JURY") });

        var result = await Create().ProcessSlackReactionRemovedAsync(Removed(":thumbsup:"));

        result.Success.Should().BeTrue();
        result.NewStatus.Should().Be(SubmissionStatus.Pending);
        submission.Status.Should().Be(SubmissionStatus.Pending);
        submission.StoragePath.Should().Be("/s/Pending/DWO 4.jpg");
        var review = submission.Reviews.OrderBy(r => r.CreatedAt).Last();
        review.Reviewer.Should().Be("U_JURY");
        review.ReactionEmoji.Should().Be("thumbsup");
        review.Notes.Should().Be("Beoordelingsemoji weggehaald");
        _notifier.Received(1).NotifyChanged();
    }

    [Fact]
    public async Task RemovedReaction_OtherReviewsLeft_ShouldApplyTheBestRemaining()
    {
        var submission = ApprovedSubmission();
        _slack.GetReactionsAsync("C1", "1.0", Arg.Any<CancellationToken>())
            .Returns(new[] { Reaction("x", "U_JURY"), Reaction("heart", "U_JURY2") });

        var result = await Create().ProcessSlackReactionRemovedAsync(Removed("thumbsup"));

        result.NewStatus.Should().Be(SubmissionStatus.Awesome);
        submission.Status.Should().Be(SubmissionStatus.Awesome);
        submission.Reviews.OrderBy(r => r.CreatedAt).Last().Reviewer.Should().Be("U_JURY2");
    }

    [Fact]
    public async Task RemovedReaction_StatusUnchanged_ShouldNotUpdate()
    {
        var submission = ApprovedSubmission();
        _slack.GetReactionsAsync("C1", "1.0", Arg.Any<CancellationToken>())
            .Returns(new[] { Reaction("thumbsup", "U_JURY2") });

        var result = await Create().ProcessSlackReactionRemovedAsync(Removed("thumbsup"));

        result.Message.Should().Be("Status blijft Goedgekeurd");
        submission.Reviews.Should().ContainSingle();
        await _submissions.DidNotReceive().UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualMode_ShouldOnlySaveTheFileWithItsName()
    {
        _runtime.SetManualMode(true);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4", "IMG_1.jpg"));

        result.Success.Should().BeTrue();
        result.Message.Should().Be("Handmatige modus: 1 bestand(en) opgeslagen");
        var storedName = $"DWO 4 {SlackTimestamp.ToDateTimeOffset("1727704800.500000")!.Value.ToLocalTime():HH-mm-ss}.jpg";
        result.StoredFiles.Should().Equal(storedName);
        result.CreatedSubmissionIds.Should().BeEmpty();
        await _storage.Received(1).SaveInitialFileAsync(_group.Name, storedName, Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _submissions.DidNotReceive().AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
        await _submissions.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _notifier.DidNotReceive().NotifyChanged();
    }

    [Fact]
    public async Task ManualMode_RedeliveredMessage_ShouldNotDownloadAgain()
    {
        _runtime.SetManualMode(true);
        var service = Create();

        await service.ProcessSlackMessageAsync(Message("Opdracht 4", "a.jpg"));
        var second = await service.ProcessSlackMessageAsync(Message("Opdracht 4", "a.jpg"));

        second.StoredFiles.Should().BeEmpty();
        await _slack.Received(1).DownloadFileAsync(Arg.Any<string>(), "a.jpg", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("hallo", "a.jpg")]      // no number
    [InlineData("Opdracht 9", "a.jpg")] // unknown assignment
    [InlineData("Opdracht 4", "a.zip")] // unsupported file
    public async Task ManualMode_UnusableMessage_ShouldBeSkippedWithoutFeedback(string text, string file)
    {
        _runtime.SetManualMode(true);
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        var result = await Create().ProcessSlackMessageAsync(Message(text, file));

        result.StoredFiles.Should().BeEmpty();
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualMode_FailedDownload_ShouldOnlyBeReported()
    {
        _runtime.SetManualMode(true);
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException());

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4", "a.jpg"));

        result.FailedDownloads.Should().Equal("a.jpg");
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualMode_ReviewerMessage_ShouldBeIgnored()
    {
        _runtime.SetManualMode(true);
        var message = Message("Opdracht 4", "a.jpg");
        message.User = "U_JURY";

        var result = await Create().ProcessSlackMessageAsync(message);

        result.Message.Should().Contain("is jurylid");
        await _slack.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualMode_Reactions_ShouldBeIgnored()
    {
        _runtime.SetManualMode(true);
        var submission = ApprovedSubmission();
        var service = Create();

        var added = await service.ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = "x", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });
        var removed = await service.ProcessSlackReactionRemovedAsync(Removed("thumbsup"));

        added.Message.Should().Be("Handmatige modus: reacties worden niet verwerkt");
        removed.Message.Should().Be("Handmatige modus: reacties worden niet verwerkt");
        submission.Status.Should().Be(SubmissionStatus.Approved);
        await _slack.DidNotReceive().GetReactionsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualMode_ShouldNotAffectRebuildImports()
    {
        _runtime.SetManualMode(true);

        var result = await Create().ImportSlackMessageAsync(Message("Opdracht 4", "a.jpg"));

        result.CreatedSubmissionIds.Should().ContainSingle();
        _added.Should().ContainSingle();
    }

    [Fact]
    public async Task ReviewerAddedInTheApp_ShouldCountRightAway()
    {
        var submission = ApprovedSubmission();
        var temp = Path.Combine(Path.GetTempPath(), $"runtime_{Guid.NewGuid():N}.json");
        var runtime = new RuntimeSettingsService(
            Options.Create(new ScoutingSettings { Reviewers = Settings.Value.Reviewers, ReviewStatusReactions = Settings.Value.ReviewStatusReactions, RuntimeSettingsFile = temp }),
            NullLogger<RuntimeSettingsService>.Instance);
        var service = new SubmissionWorkflowService(
            _submissions, _groups, _assignments, new AssignmentParser(NullLogger<AssignmentParser>.Instance),
            _storage, _slack, _notifier, runtime, Settings, NullLogger<SubmissionWorkflowService>.Instance);
        var reaction = new SlackEventItemDto { User = "U_NEW", Reaction = "heart", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" } };

        (await service.ProcessSlackReactionAsync(reaction)).Success.Should().BeFalse();
        runtime.AddReviewer("U_NEW");
        (await service.ProcessSlackReactionAsync(reaction)).Success.Should().BeTrue();

        submission.Status.Should().Be(SubmissionStatus.Awesome);
        File.Delete(temp);
    }

    [Fact]
    public async Task ImportedReactions_ForMessageWithoutSubmissions_ShouldReportStatusOnly()
    {
        var result = await Create().ApplyImportedReactionsAsync("C1", "9.9", new[] { Reaction("thumbsup", "U_JURY") });

        result.Status.Should().Be(SubmissionStatus.Approved);
        await _submissions.DidNotReceive().UpdateAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>());
    }
}
