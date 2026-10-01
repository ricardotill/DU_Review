using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class ReviewReminderServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 10, 0, 0, TimeSpan.Zero);

    private readonly TempDirectory _temp = new();
    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly List<Submission> _all = new();
    private readonly List<(string Channel, string Text)> _posted = new();
    private readonly Group _wolves = new() { Name = "De Wolven", Abbreviation = "DWO", SlackChannelId = "C1" };
    private string? _postError;

    public ReviewReminderServiceTests()
    {
        _submissions.Setup(s => s.SearchSubmissionsAsync(It.IsAny<SubmissionFilter>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _all.ToList());
        _slack.Setup(s => s.IsConnected).Returns(true);
        _slack.Setup(s => s.GetWorkspaceAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new SlackWorkspace("T1", "https://scouting.slack.com/"));
        _slack.Setup(s => s.PostMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback((string channel, string text, CancellationToken _) => _posted.Add((channel, text)))
            .ReturnsAsync(() => _postError);
    }

    public void Dispose() => _temp.Dispose();

    private (ReviewReminderService Service, RuntimeSettingsService Runtime) Create(int? minutes = 15, string? channel = "C_JURY")
    {
        var options = Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            ReviewReminder = new ReviewReminderConfig { AfterMinutes = minutes, ChannelId = channel }
        });
        var runtime = new RuntimeSettingsService(options, NullLogger<RuntimeSettingsService>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(_submissions.Object)
            .AddSingleton(_slack.Object)
            .BuildServiceProvider();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var time = new FixedTimeProvider(Now);
        var links = new SlackLinks(scopes, time, NullLogger<SlackLinks>.Instance);
        return (new ReviewReminderService(scopes, runtime, links, time, NullLogger<ReviewReminderService>.Instance), runtime);
    }

    private Submission Open(int number, TimeSpan waiting, string ts, SubmissionStatus status = SubmissionStatus.Pending)
    {
        var submission = new Submission
        {
            GroupId = _wolves.Id, Group = _wolves, AssignmentNumber = number, Status = status,
            SlackChannelId = "C1", SlackMessageId = ts, CreatedAt = Now - waiting, StoragePath = $"/s/{ts}.jpg"
        };
        _all.Add(submission);
        return submission;
    }

    [Fact]
    public async Task Remind_ShouldPostOnceForMessagesWaitingLongerThanTheTime()
    {
        Open(12, TimeSpan.FromMinutes(23), "1000.000100");
        Open(12, TimeSpan.FromMinutes(23), "1000.000100"); // second file of the same message
        Open(3, TimeSpan.FromMinutes(5), "2000.000100");
        var (service, _) = Create();

        (await service.RemindAsync()).Should().Be(1);
        (await service.RemindAsync()).Should().Be(0);

        _posted.Should().ContainSingle();
        _posted[0].Channel.Should().Be("C_JURY");
        _posted[0].Text.Should().Contain("1 inzending wacht langer dan 15 min")
            .And.Contain("<https://scouting.slack.com/archives/C1/p1000000100|DWO – opdracht 12> (23 min)")
            .And.NotContain("opdracht 3");
    }

    [Fact]
    public async Task Remind_ShouldSkipMessagesThatNoLongerCount()
    {
        Open(12, TimeSpan.FromMinutes(40), "1000.000100", SubmissionStatus.Approved);
        Open(12, TimeSpan.FromMinutes(30), "2000.000100");
        var (service, _) = Create();

        (await service.RemindAsync()).Should().Be(0);
        _posted.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "C_JURY")]
    [InlineData(15, null)]
    public async Task Remind_WithoutTimeOrChannel_ShouldPostNothing(int? minutes, string? channel)
    {
        Open(12, TimeSpan.FromHours(2), "1000.000100");
        var (service, _) = Create(minutes, channel);

        (await service.RemindAsync()).Should().Be(0);
        _posted.Should().BeEmpty();
    }

    [Fact]
    public async Task Remind_InManualMode_ShouldPostNothing()
    {
        Open(12, TimeSpan.FromHours(2), "1000.000100");
        var (service, runtime) = Create();
        runtime.SetManualMode(true);

        (await service.RemindAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Remind_ShouldFollowATimeChangedInTheApp()
    {
        Open(12, TimeSpan.FromMinutes(8), "1000.000100");
        var (service, runtime) = Create(minutes: 15);

        (await service.RemindAsync()).Should().Be(0);
        runtime.SetReviewReminderMinutes(5);
        (await service.RemindAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Remind_WhenSlackRefuses_ShouldTryAgainNextTime()
    {
        Open(12, TimeSpan.FromMinutes(20), "1000.000100");
        var (service, _) = Create();
        _postError = "not_in_channel";

        (await service.RemindAsync()).Should().Be(0);
        _postError = null;
        (await service.RemindAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Remind_ShouldListAtMostTheMaximumAndCountTheRest()
    {
        for (var i = 0; i < ReviewReminderService.MaxLines + 2; i++)
        {
            Open(i + 1, TimeSpan.FromMinutes(30 + i), $"{1000 + i}.000100");
        }

        var (service, _) = Create();
        await service.RemindAsync();

        _posted.Single().Text.Should().Contain("…en 2 meer");
        _posted.Single().Text.Split('\n').Count(l => l.StartsWith("•")).Should().Be(ReviewReminderService.MaxLines);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
