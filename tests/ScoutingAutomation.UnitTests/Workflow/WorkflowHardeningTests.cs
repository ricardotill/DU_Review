using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Files and database staying in step when a save fails, Slack events delivered twice or read late,
/// and overrides that take turns with Slack. Against a real SQLite database and real file storage.
/// </summary>
public class WorkflowHardeningTests : IDisposable
{
    private const string Ts = "1727704800.500000";

    private readonly TempDirectory _temp = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly ScoutingDbContext _db;
    private readonly FailingRepository _submissions;
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Group _group = new() { Id = Guid.NewGuid(), Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly ScoutingSettings _settings;
    private readonly SlackProcessingGate _gate = new();
    private readonly SlackOwnUploads _ownUploads = new();
    private RuntimeSettingsService? _runtime;

    public WorkflowHardeningTests()
    {
        _connection.Open();
        _db = new ScoutingDbContext(new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Groups.Add(_group);
        _db.Assignments.Add(new Assignment { Code = "4", StandardPoints = 1, AwesomePoints = 2 });
        _db.SaveChanges();
        _submissions = new FailingRepository(new SubmissionRepository(_db));

        _settings = new ScoutingSettings
        {
            StorageRoot = _temp.Path,
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            }
        };

        _slack.Setup(s => s.IsConnected).Returns(true);
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(new byte[] { 1, 2, 3 }));
        _slack.Setup(s => s.GetUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SlackUserLookup.Failed("x"));
        SlackShows(("thumbsup", "U_JURY"));
    }

    private RuntimeSettingsService Runtime => _runtime ??= TestRuntime.For(Options.Create(_settings));

    private SubmissionWorkflowService Create() => new(
        _submissions, new GroupRepository(_db), new AssignmentRepository(_db),
        new AssignmentParser(NullLogger<AssignmentParser>.Instance),
        new LocalFileStorageService(Options.Create(_settings), NullLogger<LocalFileStorageService>.Instance),
        _slack.Object, Mock.Of<ISubmissionChangeNotifier>(), Runtime, Options.Create(_settings),
        NullLogger<SubmissionWorkflowService>.Instance,
        channels: new SlackChannelLocks(), gate: _gate, ownUploads: _ownUploads);

    private void SlackShows(params (string Emoji, string User)[] reactions) =>
        _slack.Setup(s => s.GetReactionsAsync("C1", Ts, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reactions.Select(r => new SlackMessageReactionDto { Name = r.Emoji, Users = new List<string> { r.User } }).ToList());

    private static SlackEventItemDto Message(string? url = "https://files.slack.com/a.jpg", string fileId = "F1") => new()
    {
        Type = "message", Subtype = "file_share", Channel = "C1", User = "U_SCOUT", Text = "Opdracht 4", Ts = Ts,
        Files = new List<SlackFileItemDto> { new() { Id = fileId, Name = "a.jpg", Size = 3, UrlPrivate = url } }
    };

    private static SlackEventItemDto Reaction(string emoji) => new()
    {
        Type = "reaction_added", User = "U_JURY", Reaction = emoji, Item = new SlackReactionTargetDto { Channel = "C1", Ts = Ts }
    };

    private Submission Stored() => _db.Submissions.AsNoTracking().Include(s => s.Reviews).Single();

    [Fact]
    public async Task Review_WhenTheDatabaseRefusesTheSave_ShouldPutTheFileBack()
    {
        await Create().ProcessSlackMessageAsync(Message());
        var before = Stored().StoragePath;
        _submissions.FailUpdates = true;

        var act = () => Create().ProcessSlackReactionAsync(Reaction("thumbsup"));

        await act.Should().ThrowAsync<IOException>();
        File.Exists(before).Should().BeTrue("the database still points there");
        Directory.Exists(Path.Combine(_temp.Path, "de-wolven", "approved")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_temp.Path, "de-wolven", "approved")).Should().BeEmpty();
    }

    [Fact]
    public async Task NewSubmission_WhenTheDatabaseRefusesIt_ShouldLeaveNoFileBehind()
    {
        _submissions.FailAdds = true;

        var act = () => Create().ProcessSlackMessageAsync(Message());

        await act.Should().ThrowAsync<IOException>();
        Directory.GetFiles(_temp.Path, "*.jpg", SearchOption.AllDirectories).Should().BeEmpty("the catch-up tries the message again later");
    }

    [Fact]
    public async Task Reaction_DeliveredTwice_ShouldAddOneReview()
    {
        await Create().ProcessSlackMessageAsync(Message());

        await Create().ProcessSlackReactionAsync(Reaction("thumbsup"));
        var second = await Create().ProcessSlackReactionAsync(Reaction("thumbsup"));

        second.Success.Should().BeFalse();
        Stored().Reviews.Should().ContainSingle();
    }

    [Fact]
    public async Task CatchUp_WithOldReactions_ShouldNotUndoAReviewPlacedSince()
    {
        await Create().ProcessSlackMessageAsync(Message());
        await Create().ProcessSlackReactionAsync(Reaction("thumbsup"));

        // The history was read before the reviewer reacted; Slack shows the reaction now
        var changed = await Create().SyncReviewAsync("C1", Ts, Array.Empty<SlackMessageReactionDto>());

        changed.Should().BeFalse();
        Stored().Status.Should().Be(SubmissionStatus.Approved);
    }

    [Fact]
    public async Task CatchUp_WhenSlackCannotBeReadAgain_ShouldChangeNothing()
    {
        await Create().ProcessSlackMessageAsync(Message());
        await Create().ProcessSlackReactionAsync(Reaction("thumbsup"));
        _slack.Setup(s => s.GetReactionsAsync("C1", Ts, It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyList<SlackMessageReactionDto>?)null);

        var changed = await Create().SyncReviewAsync("C1", Ts, Array.Empty<SlackMessageReactionDto>());

        changed.Should().BeFalse();
        Stored().Status.Should().Be(SubmissionStatus.Approved);
    }

    [Fact]
    public async Task CatchUp_WhenSlackStillShowsNoReview_ShouldUndoIt()
    {
        await Create().ProcessSlackMessageAsync(Message());
        await Create().ProcessSlackReactionAsync(Reaction("thumbsup"));
        SlackShows();

        var changed = await Create().SyncReviewAsync("C1", Ts, Array.Empty<SlackMessageReactionDto>());

        changed.Should().BeTrue();
        Stored().Status.Should().Be(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task PictureTheAppUploaded_ShouldNeverBeASubmission()
    {
        // E.g. before the bot's own user ID is known
        _ownUploads.Add("F_OWN");

        var result = await Create().ProcessSlackMessageAsync(Message(fileId: "F_OWN"));

        result.CreatedSubmissionIds.Should().BeEmpty();
        _db.Submissions.Should().BeEmpty();
    }

    [Fact]
    public async Task FileWithoutADownloadAddress_ShouldFailInsteadOfStoringAStandIn()
    {
        var result = await Create().ProcessSlackMessageAsync(Message(url: null));

        result.FailedDownloads.Should().Equal("a.jpg");
        _db.Submissions.Should().BeEmpty();
        _slack.Verify(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ManualMode_DownloadThatFailed_ShouldBeTriedAgainNextTime()
    {
        _settings.ManualMode = true;
        _slack.SetupSequence(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("reset"))
            .ReturnsAsync(new MemoryStream(new byte[] { 1 }));

        var first = await Create().ProcessSlackMessageAsync(Message());
        var second = await Create().ProcessSlackMessageAsync(Message());

        first.FailedDownloads.Should().ContainSingle();
        second.StoredFiles.Should().ContainSingle();
    }

    [Fact]
    public async Task Override_WhileARebuildRuns_ShouldBeRefused()
    {
        await Create().ProcessSlackMessageAsync(Message());
        var id = Stored().Id;
        using var rebuild = await _gate.CloseAsync();

        var act = () => Create().OverrideStatusAsync(id, SubmissionStatus.Rejected, "test");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*opbouwen*");
        Stored().Status.Should().Be(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task Override_WithAVeryLongReason_ShouldStoreItShortened()
    {
        await Create().ProcessSlackMessageAsync(Message());
        var id = Stored().Id;

        await Create().OverrideStatusAsync(id, SubmissionStatus.Rejected, new string('x', 5000));

        Stored().Reviews.Single().Notes.Should().HaveLength(SubmissionWorkflowService.MaxOverrideReasonLength);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        _temp.Dispose();
    }

    /// <summary>The real repository, with saves that can be made to fail like a database that is gone.</summary>
    private sealed class FailingRepository(ISubmissionRepository inner) : ISubmissionRepository
    {
        public bool FailUpdates { get; set; }
        public bool FailAdds { get; set; }

        public Task<Submission?> GetByIdAsync(Guid id, CancellationToken ct = default) => inner.GetByIdAsync(id, ct);
        public Task<IReadOnlyList<Submission>> GetBySlackMessageIdAsync(string channelId, string messageId, CancellationToken ct = default) => inner.GetBySlackMessageIdAsync(channelId, messageId, ct);
        public Task<IReadOnlyList<Submission>> SearchSubmissionsAsync(SubmissionFilter filter, CancellationToken ct = default) => inner.SearchSubmissionsAsync(filter, ct);
        public Task<SubmissionStatistics> GetStatisticsAsync(CancellationToken ct = default) => inner.GetStatisticsAsync(ct);
        public Task AddAsync(Submission submission, CancellationToken ct = default) => FailAdds ? throw new IOException("database is gone") : inner.AddAsync(submission, ct);
        public Task UpdateAsync(Submission submission, CancellationToken ct = default) => FailUpdates ? throw new IOException("database is gone") : inner.UpdateAsync(submission, ct);
        public Task<bool> ExistsAsync(string channelId, string messageId, string? fileId, string fileName, CancellationToken ct = default) => inner.ExistsAsync(channelId, messageId, fileId, fileName, ct);
        public Task DeleteAsync(IReadOnlyList<Submission> submissions, CancellationToken ct = default) => inner.DeleteAsync(submissions, ct);
        public Task DeleteAllAsync(CancellationToken ct = default) => inner.DeleteAllAsync(ct);
        public Task DeleteByGroupAsync(Guid groupId, CancellationToken ct = default) => inner.DeleteByGroupAsync(groupId, ct);
    }
}
