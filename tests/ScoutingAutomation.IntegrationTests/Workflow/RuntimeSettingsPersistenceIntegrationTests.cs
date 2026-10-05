using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// Settings changed in the running app: what survives a restart, and reviewer names from Slack.
/// Live mode against a fake Slack API.
/// </summary>
public class RuntimeSettingsPersistenceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";
    private readonly FakeSlackApi _slack = new();
    private TestApp _app;

    public RuntimeSettingsPersistenceIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack, new Dictionary<string, string?>
        {
            ["Scouting:GameStartAnnouncement:AutoPost"] = "false",
            ["Scouting:GameEndAnnouncement:AutoPost"] = "false"
        });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private RuntimeSettingsService Runtime => _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();

    private static DateTime GameTimeIn(TimeSpan fromNow)
    {
        var at = DateTimeOffset.UtcNow.Add(fromNow).ToGameTime().DateTime;
        return at.AddTicks(-(at.Ticks % TimeSpan.TicksPerMinute));
    }

    [Fact]
    public async Task SettingsChangedInTheApp_ShouldSurviveARestartExceptManualMode()
    {
        var start = GameTimeIn(TimeSpan.FromHours(-1));
        var end = GameTimeIn(TimeSpan.FromHours(3));
        var posted = DateTimeOffset.UtcNow.AddMinutes(-50);
        var runtime = Runtime;
        runtime.AddReviewer("U_EXTRA").Should().BeNull();
        runtime.AddReviewer("U_WEG").Should().BeNull();
        runtime.RemoveReviewer("U_WEG").Should().BeTrue();
        runtime.SetReviewerName("U_EXTRA", "Eva").Should().BeNull();
        runtime.SetText(SlackText.TooLate, "Te laat! Het spel stopte om {end}.").Should().BeNull();
        runtime.SetBlockAttemptsAfterApproval(true);
        runtime.SetReviewReminderMinutes(45).Should().BeNull();
        runtime.SetEventStart(start).Should().BeNull();
        runtime.SetEventEnd(end).Should().BeNull();
        runtime.MarkGameStartPosted(Channel, posted);
        runtime.SetManualMode(true);

        _app = await _app.RestartAsync();

        var restarted = Runtime;
        restarted.AddedReviewers.Should().Equal("U_EXTRA");
        restarted.NameOf("U_EXTRA").Should().Be("Eva");
        restarted.HasManualName("U_EXTRA").Should().BeTrue();
        restarted.Text(SlackText.TooLate).Should().Be("Te laat! Het spel stopte om {end}.");
        restarted.BlockAttemptsAfterApproval.Should().BeTrue();
        restarted.ReviewReminderMinutes.Should().Be(45);
        restarted.EventStartGameTime.Should().Be(start);
        restarted.EventEndGameTime.Should().Be(end);
        restarted.GameStartPostedAt(Channel).Should().Be(posted);
        restarted.ManualMode.Should().BeFalse("manual mode starts from appsettings on every start");

        // And the restarted app works with them: the added reviewer's emoji counts
        using var client = _app.CreateClient();
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(client, SlackEvents.Message(Channel, ts, "Opdracht 91", SlackEvents.File("herstart.jpg")));
        await _app.SendEventAsync(client, SlackEvents.Reaction(Channel, ts, "star", "U_EXTRA"));
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts))).Single();
        submission.Status.Should().Be(SubmissionStatus.Awesome);

        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync($"/submissions/{submission.Id}")).Should().Contain("Eva");
    }

    [Fact]
    public async Task DamagedSettingsFile_ShouldNotStopTheApp()
    {
        Runtime.AddReviewer("U_EXTRA").Should().BeNull();

        // The restarted app starts when first used: the file is damaged before that
        _app = await _app.RestartAsync();
        await File.WriteAllTextAsync(Path.Combine(_app.StorageRoot, "runtime-settings.json"), "{ dit is geen json");

        Runtime.AddedReviewers.Should().BeEmpty();
        Runtime.IsReviewer(TestApp.Reviewer).Should().BeTrue("reviewers from appsettings still apply");
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetAsync("/configuration")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ReviewerNames_ShouldComeFromSlackButNeverOverwriteOnesEnteredByHand()
    {
        _slack.UserNames[TestApp.Reviewer] = "Jury Joost";
        _slack.UserNames["U_HAND"] = "Slack Naam";
        var runtime = Runtime;
        runtime.AddReviewer("U_HAND").Should().BeNull();
        runtime.SetReviewerName("U_HAND", "Hanna").Should().BeNull();
        runtime.AddReviewer("U_ONBEKEND").Should().BeNull();

        var result = await _app.GetAsync(sp => sp.GetRequiredService<ReviewerNameFetcher>().FetchAllAsync());

        result.Updated.Should().Be(1);
        result.KeptManual.Should().Be(1);
        // The other reviewers from testsettings.json are unknown to the fake Slack too
        result.Problems.Should().HaveCount(runtime.AllReviewers.Count - 2).And.Contain(p => p.Contains("Slack kent U_ONBEKEND niet"));
        runtime.NameOf(TestApp.Reviewer).Should().Be("Jury Joost");
        runtime.NameOf("U_HAND").Should().Be("Hanna");
        _slack.Calls("users.info").Should().NotContain(c => c["user"] == "U_HAND", "a name entered by hand is not even asked for");

        using var client = _app.CreateClient();
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(client, SlackEvents.Message(Channel, ts, "Opdracht 90", SlackEvents.File("naam.jpg")));
        await _app.SendEventAsync(client, SlackEvents.Reaction(Channel, ts, "thumbsup"));
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts))).Single();
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync($"/submissions/{submission.Id}")).Should().Contain("Jury Joost");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
