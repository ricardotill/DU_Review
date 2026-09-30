using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Extensions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Infrastructure;

/// <summary>
/// A full rebuild against real services, an in-memory database and a fake Slack.
/// </summary>
public class SlackRebuildServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly Mock<ISlackHistoryClient> _history = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly ServiceProvider _services;
    private readonly ScoutingSettings _settings;
    private readonly Group _wolves = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private readonly Group _owls = new() { Name = "De Bosuilen", Abbreviation = "DBU", SlackChannelId = "C2" };
    private readonly DateTimeOffset _since = DateTimeOffset.FromUnixTimeSeconds(1727690400);

    public SlackRebuildServiceTests()
    {
        _settings = new ScoutingSettings
        {
            StorageRoot = _temp.Path,
            SupportedExtensions = new List<string> { ".jpg" },
            Reviewers = new List<string> { "U_JURY", "U_JURY2" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig
            {
                Approved = new List<string> { "thumbsup" },
                Rejected = new List<string> { "x" },
                Awesome = new List<string> { "heart" }
            },
            Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" }
        };

        var databaseName = $"Rebuild_{Guid.NewGuid():N}";
        var root = new InMemoryDatabaseRoot();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ScoutingDbContext>(o => o.UseInMemoryDatabase(databaseName, root));
        services.AddScoutingInfrastructure(new ConfigurationBuilder().Build());
        services.AddSingleton<IOptions<ScoutingSettings>>(_ => Options.Create(_settings));
        services.RemoveAll<ISlackHistoryClient>();
        services.AddSingleton(_history.Object);
        services.RemoveAll<ISlackClient>();
        services.AddSingleton(_slack.Object);
        _services = services.BuildServiceProvider();

        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => new MemoryStream(new byte[] { 7 }));
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), "weg.jpg", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("gone"));

        WithDb(db =>
        {
            db.Groups.AddRange(_wolves, _owls);
            db.Assignments.AddRange(Enumerable.Range(1, 3).Select(n => new Assignment { Number = n, StandardPoints = 1, AwesomePoints = 2 }));
            db.SaveChanges();
        });
    }

    private ISlackRebuildService Service => _services.GetRequiredService<ISlackRebuildService>();

    private T WithDb<T>(Func<ScoutingDbContext, T> query)
    {
        using var scope = _services.CreateScope();
        return query(scope.ServiceProvider.GetRequiredService<ScoutingDbContext>());
    }

    private void WithDb(Action<ScoutingDbContext> action) => WithDb(db => { action(db); return 0; });

    private static SlackEventItemDto Msg(string ts, string? text, string[]? files = null, string user = "U_SCOUT", string? botId = null, string? subtype = null, params SlackMessageReactionDto[] reactions) => new()
    {
        Ts = ts, Text = text, User = user, BotId = botId, Subtype = subtype,
        Files = files?.Select(f => new SlackFileItemDto { Name = f, Size = 1, UrlPrivateDownload = $"https://files.slack.com/{f}" }).ToList(),
        Reactions = reactions.ToList()
    };

    private static SlackMessageReactionDto React(string name, params string[] users) => new() { Name = name, Users = users.ToList() };

    private static string Ts(int offsetSeconds) => $"{1727690400 + offsetSeconds}.000100";

    private void SlackHas(string channel, params SlackEventItemDto[] messages)
    {
        foreach (var m in messages) m.Channel = channel;
        _history.Setup(h => h.GetChannelMessagesAsync(channel, _since, It.IsAny<CancellationToken>())).ReturnsAsync(messages);
    }

    private async Task<string> SeedExistingSubmissionAsync()
    {
        var storage = _services.GetRequiredService<IFileStorageService>();
        var path = await storage.SaveInitialFileAsync(_wolves.Name, "OUD 1.jpg", new MemoryStream(new byte[] { 1 }));
        WithDb(db =>
        {
            var old = new Submission { GroupId = _wolves.Id, AssignmentNumber = 1, FileName = "oud.jpg", StoragePath = path, SlackChannelId = "C1", SlackMessageId = "1.0" };
            old.TransitionStatus(SubmissionStatus.Approved, path, SubmissionWorkflowService.DashboardOverrideReviewer, notes: "handmatig");
            db.Submissions.Add(old);
            db.SaveChanges();
        });
        return path;
    }

    [Fact]
    public async Task Rebuild_ShouldReplaceEverythingWithWhatIsInSlack()
    {
        await SeedExistingSubmissionAsync();
        SlackHas("C1",
            Msg(Ts(100), "Opdracht 1", new[] { "a.jpg" }, reactions: new[] { React("thumbsup", "U_JURY"), React("x", "U_JURY2") }),
            Msg(Ts(150), "Opdracht 1 opnieuw", new[] { "b.jpg" }, reactions: React("heart", "U_JURY")),
            Msg(Ts(200), "Opdracht 2", new[] { "bot.jpg" }, botId: "B1"),
            Msg(Ts(300), "Opdracht 2", new[] { "jury.jpg" }, user: "U_JURY"),
            Msg(Ts(400), "hallo", new[] { "c.jpg" }),
            Msg(Ts(500), "Opdracht 2", new[] { "data.zip", "weg.jpg" }),
            Msg(Ts(600), "Goed gedaan allemaal!"),
            Msg(Ts(700), "joined", subtype: "channel_join"));
        SlackHas("C2", Msg(Ts(250), "Opdracht 3", new[] { "d.jpg" }));
        var changes = 0;
        Service.Changed += () => Interlocked.Increment(ref changes);

        Service.TryStart(_since).Should().BeTrue();
        await Service.Completion;

        var progress = Service.Progress;
        progress.State.Should().Be(RebuildState.Completed, progress.Error);
        progress.Since.Should().Be(_since);
        progress.FinishedAt.Should().NotBeNull();
        progress.ChannelsRead.Should().Be(2);
        progress.ChannelsTotal.Should().Be(2);
        progress.MessagesFound.Should().Be(9);
        progress.MessagesProcessed.Should().Be(9);
        progress.SubmissionsImported.Should().Be(3);
        progress.ReviewsApplied.Should().Be(2);
        progress.Conflicts.Should().ContainSingle().Which.Reason.Should().Contain("Approved applied");
        progress.Skipped.Select(i => i.Reason).Should().BeEquivalentTo(
            "No valid assignment number pattern found in: 'hallo'",
            "Unsupported file: data.zip",
            "Download failed: weg.jpg");
        progress.Skipped.Should().OnlyContain(i => i.Group == "De Wolven" && i.PostedAt > _since);
        changes.Should().BeGreaterThan(5);

        var submissions = WithDb(db => db.Submissions.Include(s => s.Reviews).OrderBy(s => s.CreatedAt).ToList());
        submissions.Select(s => (s.FileName, s.Status)).Should().Equal(
            ("a.jpg", SubmissionStatus.Approved),
            ("b.jpg", SubmissionStatus.Awesome),
            ("d.jpg", SubmissionStatus.Pending));
        submissions[0].CreatedAt.Should().Be(SlackTimestamp.ToDateTimeOffset(Ts(100))!.Value);
        submissions[0].Reviews.Single().Reviewer.Should().Be("U_JURY");
        Path.GetFileName(submissions[1].StoragePath).Should().Be("DWO 1 (2).jpg", "the resubmission came second");
        submissions.Should().OnlyContain(s => File.Exists(s.StoragePath));
        _slack.Verify(s => s.AddReactionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _services.GetRequiredService<SlackProcessingGate>().IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task Rebuild_ShouldBackUpFilesAndOldSubmissionsIncludingOverrides()
    {
        var oldPath = await SeedExistingSubmissionAsync();
        SlackHas("C1");
        SlackHas("C2");

        Service.TryStart(_since);
        await Service.Completion;

        var backup = Service.Progress.BackupFolder!;
        backup.Should().StartWith(Path.Combine(_temp.Path, "_backup"));
        File.Exists(Path.Combine(backup, "de-wolven", Path.GetFileName(oldPath))).Should().BeTrue();
        File.Exists(oldPath).Should().BeFalse();
        Directory.Exists(Path.Combine(_temp.Path, "de-bosuilen", "approved")).Should().BeTrue("folders are recreated");

        using var export = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(backup, "submissions.json")));
        var old = export.RootElement.EnumerateArray().Single();
        old.GetProperty("FileName").GetString().Should().Be("oud.jpg");
        old.GetProperty("Group").GetString().Should().Be("De Wolven");
        old.GetProperty("Reviews")[0].GetProperty("Notes").GetString().Should().Be("handmatig");

        WithDb(db => db.Submissions.Count()).Should().Be(0);
    }

    [Fact]
    public async Task SlackFailure_ShouldLeaveCurrentDataUntouched()
    {
        var oldPath = await SeedExistingSubmissionAsync();
        SlackHas("C1");
        _history.Setup(h => h.GetChannelMessagesAsync("C2", It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Slack conversations.history failed: not_in_channel"));

        Service.TryStart(_since);
        await Service.Completion;

        Service.Progress.State.Should().Be(RebuildState.Failed);
        Service.Progress.Error.Should().Contain("not_in_channel");
        Service.Progress.BackupFolder.Should().BeNull();
        File.Exists(oldPath).Should().BeTrue();
        WithDb(db => db.Submissions.Count()).Should().Be(1);
        _services.GetRequiredService<SlackProcessingGate>().IsClosed.Should().BeFalse();
    }

    [Fact]
    public async Task SimulationMode_ShouldRefuseToRebuild()
    {
        _settings.Slack.SimulationMode = true;

        Service.IsAvailable.Should().BeFalse();
        Service.TryStart(_since);
        await Service.Completion;

        Service.Progress.State.Should().Be(RebuildState.Failed);
        Service.Progress.Error.Should().Contain("live mode");
        _history.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SecondStart_WhileRunning_ShouldBeRefused()
    {
        var release = new TaskCompletionSource<IReadOnlyList<SlackEventItemDto>>();
        _history.Setup(h => h.GetChannelMessagesAsync(It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>())).Returns(release.Task);

        Service.TryStart(_since).Should().BeTrue();
        Service.TryStart(_since).Should().BeFalse();
        Service.Progress.State.Should().Be(RebuildState.Running);

        release.SetResult(Array.Empty<SlackEventItemDto>());
        await Service.Completion;
        Service.TryStart(_since).Should().BeTrue("a finished rebuild can run again");
        await Service.Completion;
    }

    public void Dispose()
    {
        _services.Dispose();
        _temp.Dispose();
    }
}
