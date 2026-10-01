using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// The start and end of the event, set in the running app, in live mode against a fake Slack API.
/// </summary>
public class EventWindowIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C03WOLVEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _client = default!;
    private RuntimeSettingsService _runtime = default!;

    public EventWindowIntegrationTests(WebApplicationFactory<Program> factory)
    {
        // The announcements have their own tests; here they would only add posts
        _app = TestApp.Live(factory, _slack, new Dictionary<string, string?>
        {
            ["Scouting:GameStartAnnouncement:AutoPost"] = "false",
            ["Scouting:GameEndAnnouncement:AutoPost"] = "false"
        });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        _runtime = _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts));

    private static DateTime GameTimeIn(TimeSpan fromNow) => DateTimeOffset.UtcNow.Add(fromNow).ToGameTime().DateTime;

    [Fact]
    public async Task MessageBeforeTheStart_ShouldBeIgnoredWithoutFeedback()
    {
        _runtime.SetEventStart(GameTimeIn(TimeSpan.FromHours(1))).Should().BeNull();
        try
        {
            var early = SlackEvents.Ts();
            var onTime = SlackEvents.Ts(TimeSpan.FromHours(2).TotalSeconds);

            var ignored = await _app.SendEventAsync(_client, SlackEvents.Message(Channel, early, "Opdracht 50", SlackEvents.File("vroeg.jpg")));
            await _app.SendEventAsync(_client, SlackEvents.Message(Channel, onTime, "Opdracht 50", SlackEvents.File("optijd.jpg")));

            ignored.GetProperty("message").GetString().Should().Contain("starttijd");
            (await Submissions(early)).Should().BeEmpty();
            _slack.Writes.Should().NotContain(c => c["timestamp"] == early || c["thread_ts"] == early, "the game had not started, so there is nothing to say");
            (await Submissions(onTime)).Should().ContainSingle();
        }
        finally
        {
            _runtime.ResetEventStart();
        }
    }

    [Fact]
    public async Task MessageAfterTheEnd_ShouldBeRefusedWithTheEndTime()
    {
        _runtime.SetEventEnd(GameTimeIn(TimeSpan.FromMinutes(-30))).Should().BeNull();
        try
        {
            var late = SlackEvents.Ts();
            var onTime = SlackEvents.Ts(TimeSpan.FromHours(-1).TotalSeconds);

            await _app.SendEventAsync(_client, SlackEvents.Message(Channel, late, "Opdracht 51", SlackEvents.File("laat.jpg")));
            await _app.SendEventAsync(_client, SlackEvents.Message(Channel, onTime, "Opdracht 51", SlackEvents.File("optijd.jpg")));

            (await Submissions(late)).Should().BeEmpty();
            _slack.Calls("reactions.add").Should().ContainSingle(c => c["timestamp"] == late).Which["name"].Should().Be("alarm_clock");
            var end = _runtime.EventEnd!.Value.ToGameTime().ToString("HH:mm");
            _slack.ThreadReplies(late).Should().ContainSingle().Which["text"].Should().Contain($"om {end} afgelopen");
            (await Submissions(onTime)).Should().ContainSingle("a message posted before the end still counts when it arrives late");
        }
        finally
        {
            _runtime.ResetEventEnd();
        }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
