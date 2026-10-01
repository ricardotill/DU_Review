using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// A reviewer taking back an emoji, in live mode against a fake Slack API.
/// </summary>
public class ReactionRemovedIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C03WOLVEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;

    public ReactionRemovedIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RemovingTheOnlyReview_ShouldMakeSubmissionPendingAgain()
    {
        using var client = _app.CreateClient();
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(client, SlackEvents.Message(Channel, ts, "Opdracht 9", SlackEvents.File("foto.jpg")));
        await _app.SendEventAsync(client, SlackEvents.Reaction(Channel, ts, "thumbsup"));

        var approved = await Single(ts);
        approved.Status.Should().Be(SubmissionStatus.Approved);
        approved.StoragePath.Should().Contain(Path.Combine("de-wolven", "approved"));

        // Slack now shows no reviewer reactions on the message
        _slack.Reactions[FakeSlackApi.Key(Channel, ts)] = new[] { SlackEvents.Reacted("eyes", SlackEvents.Scout) };
        await _app.SendEventAsync(client, SlackEvents.ReactionRemoved(Channel, ts, "thumbsup"));

        var pending = await Single(ts);
        pending.Status.Should().Be(SubmissionStatus.Pending);
        Path.GetDirectoryName(pending.StoragePath).Should().Be(Path.Combine(_app.StorageRoot, "de-wolven"));
        File.Exists(pending.StoragePath).Should().BeTrue();
        pending.Reviews.First().Notes.Should().Be("Beoordelingsemoji weggehaald");
        _slack.Calls("reactions.get").Should().HaveCount(2, "both adding and removing a review read what is on the message");
    }

    [Fact]
    public async Task UnreadableReactions_ShouldLeaveTheStatusAlone()
    {
        using var client = _app.CreateClient();
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(client, SlackEvents.Message(Channel, ts, "Opdracht 10", SlackEvents.File("foto.jpg")));
        await _app.SendEventAsync(client, SlackEvents.Reaction(Channel, ts, "heart"));
        _slack.Errors["reactions.get"] = "ratelimited";

        var result = await _app.SendEventAsync(client, SlackEvents.ReactionRemoved(Channel, ts, "heart"));

        result.GetProperty("isFailure").GetBoolean().Should().BeTrue();
        (await Single(ts)).Status.Should().Be(SubmissionStatus.Awesome);
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync("/events")).Should().Contain("Reacties konden niet uit Slack worden gelezen");
    }

    private async Task<Domain.Entities.Submission> Single(string ts) =>
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts))).Single();

    public async Task DisposeAsync() => await _app.DisposeAsync();
}

/// <summary>
/// Manual mode and reviewers added on the fly, in the running app.
/// </summary>
public class RuntimeSettingsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;

    public RuntimeSettingsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory, new Dictionary<string, string?> { ["Scouting:ManualMode"] = "true" });
    }

    public Task InitializeAsync() => Task.CompletedTask;

    private static object Message(string ts, string text, string file) => new
    {
        type = "event_callback",
        @event = new
        {
            type = "message", channel = "C02BOSUILEN", user = "U_SCOUT", text, ts,
            files = new[] { new { name = file, mimetype = "image/jpeg", size = 3, url_private = $"sim://{file}" } }
        }
    };

    private Task<IReadOnlyList<Domain.Entities.Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync("C02BOSUILEN", ts));

    [Fact]
    public async Task ManualMode_ShouldOnlySaveNamedFilesUntilSwitchedOff()
    {
        using var client = _app.CreateClient();
        var runtime = _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.ManualMode.Should().BeTrue("the default comes from the settings");
        var before = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).Count;

        var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.000300";
        (await TestApp.PostEventAsync(client, Message(ts, "Opdracht 11", "IMG_0001.jpg"))).EnsureSuccessStatusCode();
        (await TestApp.PostEventAsync(client, Message(ts, "Opdracht 11", "IMG_0001.jpg"))).EnsureSuccessStatusCode(); // redelivery

        Directory.GetFiles(Path.Combine(_app.StorageRoot, "de-bosuilen"), "DBU 11*").Select(Path.GetFileName).Should().Equal($"DBU 11 {SlackTimestamp.ToDateTimeOffset(ts)!.Value.ToLocalTime():HH-mm-ss}.jpg");
        (await Submissions(ts)).Should().BeEmpty("manual mode registers nothing");
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).Count.Should().Be(before);

        runtime.SetManualMode(false);
        var nextTs = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.000301";
        (await TestApp.PostEventAsync(client, Message(nextTs, "Opdracht 12", "IMG_0002.jpg"))).EnsureSuccessStatusCode();

        (await Submissions(nextTs)).Should().ContainSingle().Which.Status.Should().Be(SubmissionStatus.Pending);
    }

    [Fact]
    public async Task ReviewerAddedOnTheFly_ShouldCountAndBeSaved()
    {
        using var client = _app.CreateClient();
        var runtime = _app.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        runtime.SetManualMode(false);
        var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.000400";
        await TestApp.PostEventAsync(client, Message(ts, "Opdracht 13", "a.jpg"));
        var reaction = new { type = "event_callback", @event = new { type = "reaction_added", user = "U_NIEUW", reaction = "heart", item = new { channel = "C02BOSUILEN", ts } } };

        await TestApp.PostEventAsync(client, reaction);
        (await Submissions(ts)).Single().Status.Should().Be(SubmissionStatus.Pending, "U_NIEUW is not a reviewer yet");

        runtime.AddReviewer("U_NIEUW").Should().BeNull();
        await TestApp.PostEventAsync(client, reaction);

        (await Submissions(ts)).Single().Status.Should().Be(SubmissionStatus.Awesome);
        (await File.ReadAllTextAsync(Path.Combine(_app.StorageRoot, "runtime-settings.json"))).Should().Contain("U_NIEUW");
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync("/configuration")).Should().Contain("U_NIEUW");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
