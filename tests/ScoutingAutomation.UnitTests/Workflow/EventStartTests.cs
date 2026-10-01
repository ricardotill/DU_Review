using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Messages posted before <c>Scouting:EventStart</c> never count.
/// </summary>
public class EventStartTests
{
    // 09:00 in Amsterdam (summer time) is 07:00 UTC: Unix 1791010800
    private static readonly DateTime Start = new(2026, 10, 3, 9, 0, 0);
    private const string AtStartTs = "1791010800.000000";
    private const string JustBeforeTs = "1791010799.999000";

    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<IAssignmentRepository> _assignments = new();
    private readonly Mock<IAssignmentParser> _parser = new();
    private readonly Mock<IFileStorageService> _storage = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Mock<ISubmissionChangeNotifier> _notifier = new();

    private static ScoutingSettings Settings(DateTime? eventStart) => new()
    {
        EventStart = eventStart,
        SupportedExtensions = new List<string> { ".jpg" },
        SlackFeedback = new SlackFeedbackConfig
        {
            MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Geen nummer" }
        }
    };

    private SubmissionWorkflowService Create(DateTime? eventStart)
    {
        var options = Options.Create(Settings(eventStart));
        return new(_submissions.Object, _groups.Object, _assignments.Object, _parser.Object, _storage.Object, _slack.Object, _notifier.Object,
            TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    private static SlackEventItemDto Message(string? ts) => new()
    {
        Channel = "C1", User = "U_SCOUT", Text = "zonder nummer", Ts = ts,
        Files = new List<SlackFileItemDto> { new() { Id = "F1", Name = "foto.jpg" } }
    };

    [Fact]
    public async Task Message_BeforeStart_ShouldBeIgnoredWithoutFeedback()
    {
        var result = await Create(Start).ProcessSlackMessageAsync(Message(JustBeforeTs));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("starttijd");
        _groups.VerifyNoOtherCalls();
        _slack.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ImportedMessage_BeforeStart_ShouldBeIgnored()
    {
        var result = await Create(Start).ImportSlackMessageAsync(Message(JustBeforeTs));

        result.Message.Should().Contain("starttijd");
        _groups.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(AtStartTs)]
    [InlineData(null)]
    [InlineData("geen tijd")]
    public async Task Message_AtStartOrWithoutValidTimestamp_ShouldBeProcessed(string? ts)
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(new Group { Name = "De Wolven", SlackChannelId = "C1" });
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Failed("Geen opdrachtnummer"));

        await Create(Start).ProcessSlackMessageAsync(Message(ts));

        // Reaching the channel lookup proves the start check let the message through
        _groups.Verify(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Message_WithoutEventStart_ShouldBeProcessed()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync((Group?)null);

        await Create(null).ProcessSlackMessageAsync(Message("1.0"));

        _groups.Verify(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void EventStart_ShouldBeReadInTheGamesTimeZone()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(Start)));

        runtime.EventStart.Should().Be(new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero));
        runtime.IsBeforeEventStart(JustBeforeTs).Should().BeTrue();
        runtime.IsBeforeEventStart(AtStartTs).Should().BeFalse();
    }

    [Fact]
    public void NotBeforeEventStart_ShouldReturnTheLaterTime()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(Start)));
        var start = new DateTimeOffset(2026, 10, 3, 7, 0, 0, TimeSpan.Zero);

        runtime.NotBeforeEventStart(start.AddHours(-3)).Should().Be(start);
        runtime.NotBeforeEventStart(start.AddHours(1)).Should().Be(start.AddHours(1));
        TestRuntime.For(Options.Create(Settings(null))).NotBeforeEventStart(start.AddHours(-3)).Should().Be(start.AddHours(-3));
    }

    [Fact]
    public void HistoryMessage_BeforeStart_ShouldNotBeACandidate()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(Start)));

        SlackRebuildService.IsCandidate(Message(JustBeforeTs), runtime).Should().BeFalse();
        SlackRebuildService.IsCandidate(Message(AtStartTs), runtime).Should().BeTrue();
    }
}
