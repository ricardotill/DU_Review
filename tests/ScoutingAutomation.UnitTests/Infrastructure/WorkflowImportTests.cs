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

namespace ScoutingAutomation.UnitTests.Infrastructure;

/// <summary>
/// Importing history, message times, failed downloads and reviews of messages with several files.
/// </summary>
public class WorkflowImportTests
{
    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<IAssignmentRepository> _assignments = new();
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Mock<ISubmissionChangeNotifier> _notifier = new();
    private readonly Group _group = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly List<Submission> _added = new();

    public WorkflowImportTests()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _assignments.Setup(a => a.GetByNumberAsync(4, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 4 });
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(new byte[] { 1 }));
        _slack.Setup(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _storage.Setup(s => s.SaveInitialFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string name, Stream _, CancellationToken _) => $"/s/{name}");
        _storage.Setup(s => s.MoveFileToStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<SubmissionStatus>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string path, SubmissionStatus status, CancellationToken _) => $"/s/{status}/{Path.GetFileName(path)}");
        _submissions.Setup(s => s.AddAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>())).Callback<Submission, CancellationToken>((s, _) => _added.Add(s));
        _submissions.Setup(s => s.GetBySlackMessageIdAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<Submission>());
    }

    private SubmissionWorkflowService Create() => new(
        _submissions.Object, _groups.Object, _assignments.Object, new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        _storage.Object, _slack.Object, _notifier.Object,
        Options.Create(new ScoutingSettings
        {
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY", "U_JURY2" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            }
        }),
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
        _assignments.Setup(a => a.GetByNumberAsync(9, It.IsAny<CancellationToken>())).ReturnsAsync((Assignment?)null);

        await Create().ImportSlackMessageAsync(Message(text, file));

        _slack.Verify(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _slack.Verify(s => s.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _slack.Setup(s => s.DownloadFileAsync("https://files.slack.com/weg.jpg", "weg.jpg", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("404"));

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4", "weg.jpg", "goed.jpg"));

        result.FailedDownloads.Should().Equal("weg.jpg");
        result.CreatedSubmissionIds.Should().ContainSingle();
        _added.Single().FileName.Should().Be("goed.jpg");
        _slack.Verify(s => s.AddReactionAsync("C1", "1727704800.500000", "warning", It.IsAny<CancellationToken>()), Times.Once);
        _slack.Verify(s => s.PostThreadReplyAsync("C1", "1727704800.500000", It.Is<string>(t => t.Contains("weg.jpg")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FailedDownload_Import_ShouldOnlyReport()
    {
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("404"));

        var result = await Create().ImportSlackMessageAsync(Message("Opdracht 4", "weg.jpg"));

        result.FailedDownloads.Should().Equal("weg.jpg");
        _slack.Verify(s => s.PostThreadReplyAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelledDownload_ShouldStopProcessing()
    {
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());

        var act = () => Create().ProcessSlackMessageAsync(Message("Opdracht 4", "a.jpg"));

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
        _submissions.Setup(s => s.GetBySlackMessageIdAsync("C1", "1.0", It.IsAny<CancellationToken>())).ReturnsAsync(files);

        var result = await Create().ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });

        result.SubmissionId.Should().Be(files[0].Id);
        files.Should().OnlyContain(f => f.Status == SubmissionStatus.Approved && f.StoragePath.StartsWith("/s/Approved/"));
        _submissions.Verify(s => s.UpdateAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        _notifier.Verify(n => n.NotifyChanged(), Times.Once);
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
        _submissions.Setup(s => s.GetBySlackMessageIdAsync("C1", "1.0", It.IsAny<CancellationToken>())).ReturnsAsync(new[] { submission });

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

    [Fact]
    public async Task ImportedReactions_ForMessageWithoutSubmissions_ShouldReportStatusOnly()
    {
        var result = await Create().ApplyImportedReactionsAsync("C1", "9.9", new[] { Reaction("thumbsup", "U_JURY") });

        result.Status.Should().Be(SubmissionStatus.Approved);
        _submissions.Verify(s => s.UpdateAsync(It.IsAny<Submission>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
