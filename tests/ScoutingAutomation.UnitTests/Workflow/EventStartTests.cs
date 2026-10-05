using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Infrastructure.Workflow;
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

    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IAssignmentParser _parser = Substitute.For<IAssignmentParser>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();

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
        return new(_submissions, _groups, _assignments, _parser, _storage, _slack, _notifier,
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
        _groups.ReceivedCalls().Should().BeEmpty();
        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ImportedMessage_BeforeStart_ShouldBeIgnored()
    {
        var result = await Create(Start).ImportSlackMessageAsync(Message(JustBeforeTs));

        result.Message.Should().Contain("starttijd");
        _groups.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(AtStartTs)]
    [InlineData(null)]
    [InlineData("geen tijd")]
    public async Task Message_AtStartOrWithoutValidTimestamp_ShouldBeProcessed(string? ts)
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(new Group { Name = "De Wolven", SlackChannelId = "C1" });
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Failed("Geen opdrachtnummer"));

        await Create(Start).ProcessSlackMessageAsync(Message(ts));

        // Reaching the channel lookup proves the start check let the message through
        await _groups.Received(1).GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_WithoutEventStart_ShouldBeProcessed()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns((Group?)null);

        await Create(null).ProcessSlackMessageAsync(Message("1.0"));

        await _groups.Received(1).GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>());
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

    [Fact]
    public void HistoryMessage_FromTheBotsOwnUser_ShouldNotBeACandidate()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(Start)));
        var message = Message(AtStartTs);
        message.User = "UBOT";

        SlackRebuildService.IsCandidate(message, runtime, botUserId: "UBOT").Should().BeFalse();
        SlackRebuildService.IsCandidate(message, runtime, botUserId: "UOTHER").Should().BeTrue();
        SlackRebuildService.IsCandidate(message, runtime).Should().BeTrue("the bot's user ID is not known");
    }
}
