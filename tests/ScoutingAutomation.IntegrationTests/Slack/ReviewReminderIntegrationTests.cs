using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using ScoutingAutomation.Infrastructure.Announcements;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Reminding the jury of submissions that wait too long, on a fake clock against a fake Slack API.
/// </summary>
public class ReviewReminderIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C04POOLVOSSEN";
    private const string JuryChannel = "C99JURY";
    private readonly FakeSlackApi _slack = new() { Workspace = ("T0SCOUTS", "https://scouts.slack.com/") };
    private readonly FakeTimeProvider _clock;
    private readonly LogCapture _logs = new(LogLevel.Information);
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public ReviewReminderIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var now = DateTimeOffset.UtcNow;
        _clock = new FakeTimeProvider(now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)));
        _app = TestApp.Live(factory, _slack,
            new Dictionary<string, string?>
            {
                ["Scouting:ReviewReminder:ChannelId"] = JuryChannel,
                ["Scouting:ReviewReminder:AfterMinutes"] = "10"
            },
            services =>
            {
                services.AddSingleton<TimeProvider>(_clock);
                services.AddSingleton<ILoggerProvider>(_logs);
            });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private IReadOnlyList<FakeSlackApi.Call> Reminders => _slack.Posts.Where(p => p["channel"] == JuryChannel).ToList();

    private ReviewReminderService Reminder => _app.Factory.Services.GetRequiredService<ReviewReminderService>();

    /// <summary>Uploads while the clock still matches the signatures of webhook requests.</summary>
    private async Task<string> UploadAsync(int number, string? reaction = null)
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, $"Opdracht {number}", SlackEvents.File($"wacht{number}.jpg")));
        if (reaction != null)
        {
            await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, reaction));
        }

        return ts;
    }

    [Fact]
    public async Task WaitingSubmission_ShouldBeRemindedOnceWithALinkToSlack()
    {
        var waiting = await UploadAsync(70);
        await UploadAsync(71, "thumbsup");

        _clock.Advance(TimeSpan.FromMinutes(11));
        // Logged once the reminder is posted and remembered
        await _logs.WaitForAsync("Reminded the jury of 1 submission(s)");

        var text = Reminders.Single()["text"];
        text.Should().Contain("1 inzending wacht langer dan 10 min op een beoordeling");
        text.Should().Contain($"<https://scouts.slack.com/archives/{Channel}/p{waiting.Replace(".", "")}|DPV – opdracht 70>");
        text.Should().NotContain("opdracht 71", "a reviewed submission is not waiting");

        (await Reminder.RemindAsync()).Should().Be(0, "each message is reminded once");
        Reminders.Should().ContainSingle();
    }

    [Fact]
    public async Task FailedReminder_ShouldBeTriedAgainOnTheNextCheck()
    {
        await UploadAsync(72);
        _slack.Errors["chat.postMessage"] = "not_in_channel";

        _clock.Advance(TimeSpan.FromMinutes(11));
        await _logs.WaitForAsync("Could not post the review reminder");
        Reminders.Should().NotBeEmpty();

        _slack.Errors.Clear();
        _clock.Advance(ReviewReminderService.CheckInterval);
        await _logs.WaitForAsync("Reminded the jury of 1 submission(s)");

        Reminders.Last()["text"].Should().Contain("opdracht 72");
        _logs.Messages.Count(m => m.StartsWith("Reminded the jury")).Should().Be(1, "once it got through, it is not repeated");
    }

    [Fact]
    public async Task ReminderTimeChangedInTheApp_ShouldApplyAtOnce()
    {
        await UploadAsync(73);
        _app.Factory.Services.GetRequiredService<RuntimeSettingsService>().SetReviewReminderMinutes(30).Should().BeNull();

        _clock.Advance(TimeSpan.FromMinutes(11));
        (await Reminder.RemindAsync()).Should().Be(0, "it waits 30 minutes now");

        _clock.Advance(TimeSpan.FromMinutes(20));
        await _logs.WaitForAsync("Reminded the jury of 1 submission(s)");
        Reminders.Should().ContainSingle().Which["text"].Should().Contain("langer dan 30 min");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
