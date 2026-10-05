using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Several files per message, duplicate deliveries, disagreeing reviewers, and edited or deleted messages,
/// against a real SQLite database (so the unique index applies) and real file storage.
/// </summary>
public class GameDayWorkflowTests : IDisposable
{
    private const string Ts = "1727704800.500000"; // 30 Sep 2024 14:00:00 UTC = 16:00:00 in Amsterdam
    private const string Time = "16-00-00";

    private readonly TempDirectory _temp = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ScoutingDbContext _db;
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly Group _group = new() { Id = Guid.NewGuid(), Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly IOptions<ScoutingSettings> _settings;

    public GameDayWorkflowTests()
    {
        _connection.Open();
        _db = new ScoutingDbContext(new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Groups.Add(_group);
        _db.Assignments.AddRange(new Assignment { Code = "4", StandardPoints = 1, AwesomePoints = 2 }, new Assignment { Code = "7", StandardPoints = 1, AwesomePoints = 2 });
        _db.SaveChanges();

        _settings = Options.Create(new ScoutingSettings
        {
            StorageRoot = _temp.Path,
            SupportedExtensions = new List<string> { ".jpg", ".png" },
            Reviewers = new List<string> { "U_JURY", "U_JURY2" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            },
            SlackFeedback = new SlackFeedbackConfig
            {
                MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Geen nummer" },
                UnknownAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Bestaat niet" }
            }
        });

        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(new byte[] { 1, 2, 3 }));
        _slack.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private SubmissionWorkflowService Create() => new(
        new SubmissionRepository(_db), new GroupRepository(_db), new AssignmentRepository(_db),
        new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        new LocalFileStorageService(_settings, NullLogger<LocalFileStorageService>.Instance),
        _slack, Substitute.For<ISubmissionChangeNotifier>(), TestRuntime.For(_settings), _settings,
        NullLogger<SubmissionWorkflowService>.Instance);

    private static SlackFileItemDto File(string name, string? id = null) =>
        new() { Id = id ?? $"F_{name}", Name = name, Size = 3, UrlPrivate = $"https://files.slack.com/{name}" };

    private static SlackEventItemDto Message(string text, params SlackFileItemDto[] files) => new()
    {
        Type = "message", Subtype = "file_share", Channel = "C1", User = "U_SCOUT", Text = text, Ts = Ts, Files = files.ToList()
    };

    private List<Submission> Stored() => _db.Submissions.AsNoTracking().OrderBy(s => s.StoragePath).ToList();

    private static string Name(Submission s) => Path.GetFileName(s.StoragePath);

    [Fact]
    public async Task MessageWithSeveralFiles_ShouldNumberThemInSlackOrder()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg"), File("b.png"), File("notes.zip"), File("c.jpg")));

        Stored().Select(Name).Should().Equal($"DWO 4 {Time} (1).jpg", $"DWO 4 {Time} (2).png", $"DWO 4 {Time} (3).jpg");
        Stored().Should().OnlyContain(s => System.IO.File.Exists(s.StoragePath));
    }

    [Fact]
    public async Task MessageWithOneFile_ShouldHaveNoNumber()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg"), File("notes.zip")));

        Stored().Select(Name).Should().Equal($"DWO 4 {Time}.jpg");
    }

    [Fact]
    public async Task FilesWithTheSameName_ShouldAllBeRegistered()
    {
        // Slack's phone app can give every photo of one post the same name
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("image.png", "F1"), File("image.png", "F2"), File("image.png", "F3")));

        Stored().Should().HaveCount(3);
        Stored().Select(s => s.SlackFileId).Should().BeEquivalentTo("F1", "F2", "F3");
        Stored().Select(Name).Should().Equal($"DWO 4 {Time} (1).png", $"DWO 4 {Time} (2).png", $"DWO 4 {Time} (3).png");
    }

    [Fact]
    public async Task SameEventTwice_ShouldRegisterEachFileOnce()
    {
        var message = Message("Opdracht 4", File("a.jpg"), File("b.jpg"));

        await Create().ProcessSlackMessageAsync(message);
        var again = await Create().ProcessSlackMessageAsync(message);

        again.CreatedSubmissionIds.Should().BeEmpty();
        Stored().Should().HaveCount(2);
        Directory.GetFiles(Path.Combine(_temp.Path, "de-wolven")).Should().HaveCount(2);
    }

    [Fact]
    public async Task SameFileAddedTwiceAtOnce_ShouldBeRefusedByTheDatabase()
    {
        var repository = new SubmissionRepository(_db);
        Submission New() => new()
        {
            GroupId = _group.Id, SlackChannelId = "C1", SlackMessageId = Ts, SlackFileId = "F1",
            FileName = "a.jpg", StoragePath = "/x", Submitter = "U_SCOUT"
        };

        await repository.AddAsync(New());
        var act = () => repository.AddAsync(New());

        await act.Should().ThrowAsync<DuplicateSubmissionException>();
        // The refused copy is no longer tracked, so later saves still work
        await repository.AddAsync(New().With(s => s.SlackFileId = "F2"));
        Stored().Should().HaveCount(2);
    }

    [Fact]
    public async Task DownloadFailingHalfway_ShouldLeaveNoFileAndAskToPostAgain()
    {
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new FailingStream());

        var result = await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("video.jpg")));

        result.FailedDownloads.Should().Equal("video.jpg");
        Stored().Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_temp.Path, "de-wolven"), "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task AddingAWorseReview_ShouldKeepTheBestOne()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        _slack.GetReactionsAsync("C1", Ts, Arg.Any<CancellationToken>())
            .Returns(new List<SlackMessageReactionDto>
        {
            new() { Name = "heart", Users = new List<string> { "U_JURY" } },
            new() { Name = "thumbsup", Users = new List<string> { "U_JURY2" } }
        });

        var result = await Create().ProcessSlackReactionAsync(Reaction("thumbsup", "U_JURY2"));

        result.NewStatus.Should().Be(SubmissionStatus.Awesome, "the same rule as removing an emoji and rebuilding");
        Stored().Single().Status.Should().Be(SubmissionStatus.Awesome);
    }

    [Fact]
    public async Task AddedReview_WhenSlackCannotListReactions_ShouldUseTheAddedEmoji()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        _slack.GetReactionsAsync("C1", Ts, Arg.Any<CancellationToken>()).Returns((IReadOnlyList<SlackMessageReactionDto>?)null);

        var result = await Create().ProcessSlackReactionAsync(Reaction("x", "U_JURY"));

        result.NewStatus.Should().Be(SubmissionStatus.Rejected);
    }

    private static SlackEventItemDto Reaction(string emoji, string user) => new()
    {
        Type = "reaction_added", User = user, Reaction = emoji, Item = new SlackReactionTargetDto { Type = "message", Channel = "C1", Ts = Ts }
    };

    private static SlackEventItemDto Edit(string? before, string after, params SlackFileItemDto[] files) => new()
    {
        Type = "message", Subtype = "message_changed", Channel = "C1",
        Message = new SlackEventItemDto { Type = "message", User = "U_SCOUT", Text = after, Ts = Ts, Files = files.ToList() },
        PreviousMessage = before == null ? null : new SlackEventItemDto { Type = "message", User = "U_SCOUT", Text = before, Ts = Ts, Files = files.ToList() }
    };

    [Fact]
    public async Task EditAddingTheNumber_ShouldRegisterTheSubmissionAndTakeBackTheQuestion()
    {
        await Create().ProcessSlackMessageAsync(Message("kijk!", File("a.jpg")));
        Stored().Should().BeEmpty();

        var result = await Create().ProcessSlackMessageChangedAsync(Edit("kijk!", "kijk! Opdracht 4", File("a.jpg")));

        result.CreatedSubmissionIds.Should().HaveCount(1);
        Stored().Single().AssignmentCode.Should().Be("4");
        Name(Stored().Single()).Should().Be($"DWO 4 {Time}.jpg");
        await _slack.Received(1).RemoveReactionAsync("C1", Ts, "question", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditAfterTheEventEnd_OfAMessageFromBefore_ShouldStillCount()
    {
        // The message was posted at 16:00, the game ended at 16:30, the number is added now
        _settings.Value.EventEnd = new DateTime(2024, 9, 30, 16, 30, 0);
        await Create().ProcessSlackMessageAsync(Message("kijk!", File("a.jpg")));

        var result = await Create().ProcessSlackMessageChangedAsync(Edit("kijk!", "kijk! Opdracht 4", File("a.jpg")));

        result.CreatedSubmissionIds.Should().HaveCount(1);
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), "alarm_clock", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EditWithoutTextChange_ShouldDoNothing()
    {
        // Slack sends an edit when the bot's thread reply changes the reply count
        await Create().ProcessSlackMessageAsync(Message("kijk!", File("a.jpg")));
        _slack.ClearReceivedCalls();

        var result = await Create().ProcessSlackMessageChangedAsync(Edit("kijk!", "kijk!", File("a.jpg")));

        result.Success.Should().BeFalse();
        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task EditChangingTheNumber_ShouldMoveSubmissionsAndRenameFiles()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg"), File("b.jpg")));
        await Create().ProcessSlackReactionAsync(Reaction("thumbsup", "U_JURY"));

        var result = await Create().ProcessSlackMessageChangedAsync(Edit("Opdracht 4", "Opdracht 7", File("a.jpg"), File("b.jpg")));

        result.Success.Should().BeTrue();
        Stored().Should().OnlyContain(s => s.AssignmentCode == "7" && s.Status == SubmissionStatus.Approved);
        Stored().Select(Name).Should().Equal($"DWO 7 {Time} (1).jpg", $"DWO 7 {Time} (2).jpg");
        Stored().Should().OnlyContain(s => System.IO.File.Exists(s.StoragePath) && s.StoragePath.Contains("approved"));
    }

    [Fact]
    public async Task EditWithAnInvalidNumber_ShouldKeepTheSubmission()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));

        await Create().ProcessSlackMessageChangedAsync(Edit("Opdracht 4", "Opdracht 99", File("a.jpg")));

        Stored().Single().AssignmentCode.Should().Be("4");
        Name(Stored().Single()).Should().Be($"DWO 4 {Time}.jpg");
    }

    [Fact]
    public async Task DeletedMessage_ShouldRemoveSubmissionsAndKeepTheFiles()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg"), File("b.jpg")));
        await Create().ProcessSlackReactionAsync(Reaction("heart", "U_JURY"));

        var result = await Create().ProcessSlackMessageDeletedAsync(new SlackEventItemDto
        {
            Type = "message", Subtype = "message_deleted", Channel = "C1", DeletedTs = Ts
        });

        result.Success.Should().BeTrue();
        Stored().Should().BeEmpty();
        _db.Reviews.Should().BeEmpty();
        Directory.GetFiles(Path.Combine(_temp.Path, "_deleted", "de-wolven")).Select(Path.GetFileName)
            .Should().BeEquivalentTo($"DWO 4 {Time} (1).jpg", $"DWO 4 {Time} (2).jpg");
    }

    [Fact]
    public async Task FileDeletedFromMessage_ShouldRemoveOnlyThatSubmission()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg"), File("b.jpg")));
        var tombstone = new SlackFileItemDto { Id = "F_b.jpg", Mode = "tombstone" };

        await Create().ProcessSlackMessageChangedAsync(Edit("Opdracht 4", "Opdracht 4", File("a.jpg"), tombstone));

        Stored().Select(s => s.SlackFileId).Should().Equal("F_a.jpg");
    }

    private static List<SlackMessageReactionDto> Reactions(params (string Emoji, string User)[] reactions) =>
        reactions.Select(r => new SlackMessageReactionDto { Name = r.Emoji, Users = new List<string> { r.User } }).ToList();

    [Fact]
    public async Task CatchUp_ReviewPlacedWhileAway_ShouldBeApplied()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));

        var changed = await Create().SyncReviewAsync("C1", Ts, Reactions(("heart", "U_JURY")));

        changed.Should().BeTrue();
        Stored().Single().Status.Should().Be(SubmissionStatus.Awesome);
    }

    [Fact]
    public async Task CatchUp_NothingChanged_ShouldAddNoReview()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        await Create().SyncReviewAsync("C1", Ts, Reactions(("thumbsup", "U_JURY")));

        var changed = await Create().SyncReviewAsync("C1", Ts, Reactions(("thumbsup", "U_JURY")));

        changed.Should().BeFalse("every reconnect runs a catch-up; the history must not fill up");
        _db.Reviews.Count().Should().Be(1);
    }

    [Fact]
    public async Task CatchUp_ReviewTakenBackWhileAway_ShouldMakeItPendingAgain()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        await Create().ProcessSlackReactionAsync(Reaction("thumbsup", "U_JURY"));

        var changed = await Create().SyncReviewAsync("C1", Ts, Reactions(("eyes", "U_SCOUT")));

        changed.Should().BeTrue();
        Stored().Single().Status.Should().Be(SubmissionStatus.Pending);
        Path.GetDirectoryName(Stored().Single().StoragePath).Should().Be(Path.Combine(_temp.Path, "de-wolven"));
    }

    [Fact]
    public async Task CatchUp_OverrideWithoutReviewInSlack_ShouldBeKept()
    {
        // The override stood in for Slack while Slack had problems
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        await Create().OverrideStatusAsync(Stored().Single().Id, SubmissionStatus.Approved, "Slack lag eruit");

        var changed = await Create().SyncReviewAsync("C1", Ts, Reactions());

        changed.Should().BeFalse();
        Stored().Single().Status.Should().Be(SubmissionStatus.Approved);
    }

    [Fact]
    public async Task CatchUp_SlackReviewAfterOverride_ShouldWin()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));
        await Create().OverrideStatusAsync(Stored().Single().Id, SubmissionStatus.Approved, "Slack lag eruit");

        await Create().SyncReviewAsync("C1", Ts, Reactions(("x", "U_JURY")));

        Stored().Single().Status.Should().Be(SubmissionStatus.Rejected, "Slack is leading");
    }

    [Fact]
    public async Task CatchUp_MessageEditedWhileAway_ShouldBeRegisteredAndLoseTheQuestion()
    {
        var result = await Create().ProcessMissedMessageAsync(Message("kijk! Opdracht 4", File("a.jpg")));

        result.CreatedSubmissionIds.Should().HaveCount(1);
        await _slack.Received(1).RemoveReactionAsync("C1", Ts, "question", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CatchUp_MessageSeenBefore_ShouldNotBeRegisteredAgain()
    {
        await Create().ProcessSlackMessageAsync(Message("Opdracht 4", File("a.jpg")));

        var result = await Create().ProcessMissedMessageAsync(Message("Opdracht 4", File("a.jpg")));

        result.CreatedSubmissionIds.Should().BeEmpty();
        Stored().Should().HaveCount(1);
        await _slack.DidNotReceive().RemoveReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _temp.Dispose();
    }

    /// <summary>A download that breaks off after the first bytes, like a dropped connection.</summary>
    private sealed class FailingStream : MemoryStream
    {
        private bool _sent;

        public FailingStream() : base(new byte[] { 1, 2, 3 }) { }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_sent) throw new IOException("connection reset");
            _sent = true;
            return base.ReadAsync(buffer, ct);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
    }
}

internal static class SubmissionTestExtensions
{
    public static Submission With(this Submission submission, Action<Submission> change)
    {
        change(submission);
        return submission;
    }
}
