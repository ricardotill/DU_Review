using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Submissions posted from <c>Scouting:EventEnd</c> on never count; the bot says so.
/// </summary>
public class EventEndTests
{
    // 17:00 in Amsterdam (summer time) is 15:00 UTC: Unix 1791039600
    private static readonly DateTime End = new(2026, 10, 3, 17, 0, 0);
    private const string AtEndTs = "1791039600.000000";
    private const string JustBeforeTs = "1791039599.999000";

    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IAssignmentParser _parser = Substitute.For<IAssignmentParser>();
    private readonly IFileStorageService _storage = Substitute.For<IFileStorageService>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();

    public EventEndTests()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(new Group { Name = "De Wolven", SlackChannelId = "C1" });
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Failed("Geen opdrachtnummer"));
        _slack.AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static ScoutingSettings Settings(DateTime? eventEnd) => new()
    {
        EventEnd = eventEnd,
        SupportedExtensions = new List<string> { ".jpg" },
        SlackFeedback = new SlackFeedbackConfig
        {
            MissingAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Geen nummer" },
            TooLate = new FeedbackConfig { Reaction = "alarm_clock", Message = "Afgelopen om {end}" }
        }
    };

    private SubmissionWorkflowService Create(DateTime? eventEnd)
    {
        var options = Options.Create(Settings(eventEnd));
        return new(_submissions, _groups, _assignments, _parser, _storage, _slack, _notifier,
            TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    private static SlackEventItemDto Message(string? ts, bool withFile = true) => new()
    {
        Channel = "C1", User = "U_SCOUT", Text = "Opdracht 12", Ts = ts,
        Files = withFile ? new List<SlackFileItemDto> { new() { Id = "F1", Name = "foto.jpg" } } : null
    };

    [Theory]
    [InlineData(AtEndTs)]
    [InlineData("1791043200.000000")]
    public async Task Message_AtOrAfterEnd_ShouldBeRejectedWithFeedback(string ts)
    {
        var result = await Create(End).ProcessSlackMessageAsync(Message(ts));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("eindtijd");
        await _slack.Received(1).AddReactionAsync("C1", ts, "alarm_clock", Arg.Any<CancellationToken>());
        await _slack.Received(1).PostThreadReplyAsync("C1", ts, "Afgelopen om 17:00", Arg.Any<CancellationToken>());
        _parser.DidNotReceive().Parse(Arg.Any<string>());
        _storage.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task ImportedMessage_AfterEnd_ShouldBeRejectedSilently()
    {
        var result = await Create(End).ImportSlackMessageAsync(Message(AtEndTs));

        result.Message.Should().Contain("eindtijd");
        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task MessageWithoutFiles_AfterEnd_ShouldGetNoFeedback()
    {
        await Create(End).ProcessSlackMessageAsync(Message(AtEndTs, withFile: false));

        _slack.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(JustBeforeTs)]
    [InlineData(null)]
    [InlineData("geen tijd")]
    public async Task Message_BeforeEndOrWithoutValidTimestamp_ShouldBeProcessed(string? ts)
    {
        await Create(End).ProcessSlackMessageAsync(Message(ts));

        // Reaching the parser proves the end check let the message through
        _parser.Received(1).Parse(Arg.Any<string>());
        await _slack.DidNotReceive().AddReactionAsync(Arg.Any<string>(), Arg.Any<string>(), "alarm_clock", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Message_WithoutEventEnd_ShouldBeProcessed()
    {
        await Create(null).ProcessSlackMessageAsync(Message("9999999999.000000"));

        _parser.Received(1).Parse(Arg.Any<string>());
    }

    [Fact]
    public void EventEnd_ShouldBeReadInTheGamesTimeZone()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(End)));

        runtime.EventEnd.Should().Be(new DateTimeOffset(2026, 10, 3, 15, 0, 0, TimeSpan.Zero));
        runtime.IsAfterEventEnd(JustBeforeTs).Should().BeFalse();
        runtime.IsAfterEventEnd(AtEndTs).Should().BeTrue();
    }

    [Fact]
    public void HistoryMessage_AfterEnd_ShouldNotBeACandidate()
    {
        var runtime = TestRuntime.For(Options.Create(Settings(End)));

        SlackRebuildService.IsCandidate(Message(AtEndTs), runtime).Should().BeFalse();
        SlackRebuildService.IsCandidate(Message(JustBeforeTs), runtime).Should().BeTrue();
    }
}
