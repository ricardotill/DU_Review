using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Picking up what Slack sent while the app was down: at startup with the Events API, against a fake Slack API.
/// </summary>
public class SlackCatchUpIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private readonly string _missed = SlackEvents.Ts(-600);
    private readonly string _reviewed = SlackEvents.Ts(-500);
    private readonly string _noNumber = SlackEvents.Ts(-400);

    public SlackCatchUpIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // What happened in Slack while the app was down
        _slack.History[Channel] = new[]
        {
            SlackEvents.HistoryMessage(_missed, "Opdracht 60", new[] { SlackEvents.File("gemist.jpg") }),
            SlackEvents.HistoryMessage(_reviewed, "Opdracht 61", new[] { SlackEvents.File("beoordeeld.jpg") },
                new[] { SlackEvents.Reacted("heart", TestApp.Reviewer), SlackEvents.Reacted("eyes", SlackEvents.Scout) }),
            SlackEvents.HistoryMessage(_noNumber, "hier is hij", new[] { SlackEvents.File("zonder.jpg") }),
            SlackEvents.HistoryMessage(SlackEvents.Ts(-300), "Opdracht 62", new[] { SlackEvents.File("jury.jpg") }, user: TestApp.Reviewer),
            SlackEvents.HistoryMessage(SlackEvents.Ts(-200), "Opdracht 63", Array.Empty<object>())
        };
        _slack.HistoryErrors["C02BOSUILEN"] = "not_in_channel";
        _app = TestApp.Live(factory, _slack);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private ISlackCatchUpService CatchUp => _app.Factory.Services.GetRequiredService<ISlackCatchUpService>();

    private Task<IReadOnlyList<Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts));

    private async Task RunAgainAsync()
    {
        CatchUp.Request("test");
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Startup_ShouldRegisterMissedSubmissionsReviewsAndFeedback()
    {
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        var result = CatchUp.LastResult;
        result.Should().NotBeNull();
        result!.MessagesChecked.Should().Be(3, "the reviewer's message and the one without files are no submissions");
        result.SubmissionsAdded.Should().Be(2);
        result.ReviewsUpdated.Should().Be(1);
        result.ChannelErrors.Should().ContainSingle().Which.Should().Be("De Bosuilen: not_in_channel");

        (await Submissions(_missed)).Single().Status.Should().Be(SubmissionStatus.Pending);
        var reviewed = (await Submissions(_reviewed)).Single();
        reviewed.Status.Should().Be(SubmissionStatus.Awesome);
        reviewed.Reviews.First().Reviewer.Should().Be(TestApp.Reviewer);

        (await Submissions(_noNumber)).Should().BeEmpty();
        _slack.ThreadReplies(_noNumber).Should().ContainSingle("a missed message gets the feedback it would have had live");
    }

    [Fact]
    public async Task RunningAgain_ShouldNotDuplicateAnythingOrRepeatFeedback()
    {
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var downloads = _slack.Calls("file").Count;

        await RunAgainAsync();

        CatchUp.LastResult!.SubmissionsAdded.Should().Be(0);
        CatchUp.LastResult.ReviewsUpdated.Should().Be(0);
        (await Submissions(_missed)).Should().ContainSingle();
        _slack.Calls("file").Count.Should().Be(downloads, "files already stored are not downloaded again");
        _slack.ThreadReplies(_noNumber).Should().ContainSingle();
    }

    [Fact]
    public async Task NumberAddedWhileAway_ShouldRegisterAndWithdrawTheQuestionMark()
    {
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        _slack.History[Channel] = new[] { SlackEvents.HistoryMessage(_noNumber, "Opdracht 64: hier is hij", new[] { SlackEvents.File("zonder.jpg") }) };

        await RunAgainAsync();

        (await Submissions(_noNumber)).Single().AssignmentNumber.Should().Be(64);
        _slack.Calls("reactions.remove").Should().Contain(c => c["timestamp"] == _noNumber && c["name"] == "question");
    }

    [Fact]
    public async Task ReviewTakenBackWhileAway_ShouldBeUndoneButADashboardOverrideKept()
    {
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var missed = (await Submissions(_missed)).Single();
        await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionWorkflowService>().OverrideStatusAsync(missed.Id, SubmissionStatus.Approved, "Slack lag eruit"));
        _slack.History[Channel] = new[]
        {
            SlackEvents.HistoryMessage(_missed, "Opdracht 60", new[] { SlackEvents.File("gemist.jpg") }),
            SlackEvents.HistoryMessage(_reviewed, "Opdracht 61", new[] { SlackEvents.File("beoordeeld.jpg") })
        };

        await RunAgainAsync();

        var undone = (await Submissions(_reviewed)).Single();
        undone.Status.Should().Be(SubmissionStatus.Pending);
        undone.Reviews.First().Reviewer.Should().Be(SubmissionWorkflowService.CatchUpReviewer);
        (await Submissions(_missed)).Single().Status.Should().Be(SubmissionStatus.Approved, "Slack never shows an override, so it says nothing about one");
    }

    [Fact]
    public async Task ManualMode_ShouldSkipCatchingUp()
    {
        await CatchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        var runtime = _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        var later = SlackEvents.Ts(-100);
        _slack.History[Channel] = new[] { SlackEvents.HistoryMessage(later, "Opdracht 65", new[] { SlackEvents.File("handmatig.jpg") }) };
        runtime.SetManualMode(true);
        var historyReads = _slack.Calls("conversations.history").Count;

        await RunAgainAsync();

        (await Submissions(later)).Should().BeEmpty();
        _slack.Calls("conversations.history").Count.Should().Be(historyReads);

        runtime.SetManualMode(false);
        await RunAgainAsync();
        (await Submissions(later)).Should().ContainSingle();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
