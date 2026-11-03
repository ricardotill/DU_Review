using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// With BlockAttemptsAfterApproval on, a group cannot send a new attempt for an assignment it already has approved or awesome.
/// </summary>
public class AttemptAfterApprovalTests : IDisposable
{
    private const string Ts = "1727704800.500000";

    private readonly TempDirectory _temp = new();
    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
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
        _runtime = TestRuntime.For(_settings);

        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _groups.GetByIdAsync(_group.Id, Arg.Any<CancellationToken>()).Returns(_group);
        _assignments.GetByCodeAsync(Arg.Is<string>(c => c == "4" || c == "5"), Arg.Any<CancellationToken>())
            .Returns(ci => new Assignment { Code = ci.ArgAt<string>(0) });
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => new MemoryStream(new byte[] { 1 }));
        _slack.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _storage.SaveInitialFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/s/{ci.ArgAt<string>(1)}");
        _storage.RenameFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"/s/{ci.ArgAt<string>(1)}");
        _submissions.When(s => s.AddAsync(Arg.Any<Submission>(), Arg.Any<CancellationToken>())).Do(ci => _added.Add(ci.ArgAt<Submission>(0)));
        _submissions.GetBySlackMessageIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var channel = ci.ArgAt<string>(0); var ts = ci.ArgAt<string>(1); return _existing.Where(s => s.SlackChannelId == channel && s.SlackMessageId == ts).ToList(); });
        _submissions.SearchSubmissionsAsync(Arg.Any<SubmissionFilter>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var f = ci.ArgAt<SubmissionFilter>(0); return _existing
                .Where(s => f.GroupId == null || s.GroupId == f.GroupId)
                .Where(s => f.AssignmentCode == null || s.AssignmentCode == f.AssignmentCode)
                .ToList(); });
    }

    public void Dispose() => _temp.Dispose();

    private SubmissionWorkflowService Create() => new(
        _submissions, _groups, _assignments, new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        _storage, _slack, _notifier, _runtime, _settings,
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
            GroupId = _group.Id, Group = _group, AssignmentCode = number.ToString(), Status = status,
            SlackChannelId = "C1", SlackMessageId = ts, FileName = "eerder.jpg", StoragePath = $"/s/eerder-{ts}.jpg",
            CreatedAt = SlackTimestamp.ToDateTimeOffset(ts)!.Value
        };
        _existing.Add(submission);
        return submission;
    }

    private void VerifyBlockedFeedback(int number) =>
        _slack.Received(1).PostThreadReplyAsync("C1", Ts, Arg.Is<string>(t => t.Contains($"Opdracht {number} is al goedgekeurd")), Arg.Any<CancellationToken>());

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
        await _slack.DidNotReceive().DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _slack.Received(1).AddReactionAsync("C1", Ts, "lock", Arg.Any<CancellationToken>());
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
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), "lock", Arg.Any<CancellationToken>());
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
        _submissions.ExistsAsync("C1", Ts, "F1", "a.jpg", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.Message.Should().NotContain("al goedgekeurd");
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatchUp_OfARegisteredEarlierAttempt_ShouldNotBeBlocked()
    {
        // Rejected first, approved in a later message: the catch-up reads the rejected one again
        var rejected = Earlier(4, SubmissionStatus.Rejected, ts: Ts);
        Earlier(4, SubmissionStatus.Approved, ts: "1727708400.000100");
        _submissions.ExistsAsync("C1", Ts, "F1", "a.jpg", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Create().ProcessMissedMessageAsync(Message("Opdracht 4"));

        result.Message.Should().NotContain("al goedgekeurd");
        rejected.Status.Should().Be(SubmissionStatus.Rejected);
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), "lock", Arg.Any<CancellationToken>());
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatchUp_OfTwoApprovedMessages_ShouldNotBlockEitherOne()
    {
        // One photo per message, both approved: neither is an attempt after the other's approval
        Earlier(4, SubmissionStatus.Approved, ts: Ts);
        Earlier(4, SubmissionStatus.Approved, ts: "1727700000.000100");
        _submissions.ExistsAsync("C1", Ts, "F1", "a.jpg", Arg.Any<CancellationToken>()).Returns(true);

        var result = await Create().ProcessMissedMessageAsync(Message("Opdracht 4"));

        result.Message.Should().NotContain("al goedgekeurd");
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissedMessage_PostedBeforeTheApproval_ShouldBeStored()
    {
        // The app was down when this attempt came in; the approved one was posted after it
        Earlier(4, SubmissionStatus.Approved, ts: "1727708400.000100");

        var result = await Create().ProcessMissedMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().ContainSingle();
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), "lock", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissedMessage_PostedAfterTheApproval_ShouldStillBeBlocked()
    {
        Earlier(4, SubmissionStatus.Approved);

        var result = await Create().ProcessMissedMessageAsync(Message("Opdracht 4"));

        result.CreatedSubmissionIds.Should().BeEmpty();
        VerifyBlockedFeedback(4);
    }

    [Fact]
    public async Task ManualMode_ShouldStillSaveTheFile()
    {
        Earlier(4, SubmissionStatus.Approved);
        _runtime.SetManualMode(true);

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4"));

        result.StoredFiles.Should().ContainSingle();
        await _slack.DidNotReceive().PostThreadReplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        mine.AssignmentCode.Should().Be("4");
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
