using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Rebuilding from Slack in the running app, against a fake Slack API.
/// </summary>
public class SlackRebuildIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Secret = "rebuild-secret";
    private readonly DateTimeOffset _since = DateTimeOffset.UtcNow.AddHours(-1);
    private readonly TaskCompletionSource _releaseHistory = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _historyRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TestApp _app;

    public SlackRebuildIntegrationTests(WebApplicationFactory<Program> factory)
    {
        var slack = new FakeSlack(this);
        _app = new TestApp(factory,
            new Dictionary<string, string?>
            {
                ["Scouting:Slack:SimulationMode"] = "false",
                ["Scouting:Slack:SigningSecret"] = Secret,
                ["Scouting:Slack:BotToken"] = "xoxb-test",
                ["Scouting:Reviewers:0"] = "U_JURY"
            },
            services =>
            {
                services.AddHttpClient<ISlackHistoryClient, SlackHistoryClient>().ConfigurePrimaryHttpMessageHandler(() => slack);
                services.AddHttpClient<ISlackClient, SlackClient>().ConfigurePrimaryHttpMessageHandler(() => slack);
            });
    }

    private string Ts(int seconds) => $"{_since.AddSeconds(seconds).ToUnixTimeSeconds()}.000100";

    public Task InitializeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Rebuild_ShouldReplaceDataWithSlackAndHoldLiveEventsUntilDone()
    {
        using var client = _app.CreateClient();
        var repository = () => _app.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<ISubmissionRepository>();
        (await repository().SearchSubmissionsAsync(new SubmissionFilter())).Should().BeEmpty("live mode never adds sample data");

        // Something the app registered earlier, which the rebuild must back up and replace
        var storage = _app.Factory.Services.GetRequiredService<IFileStorageService>();
        var earlierPath = await storage.SaveInitialFileAsync("De Bosuilen", "DBU 1.jpg", new MemoryStream(new byte[] { 9 }));
        var bosuilen = await _app.Factory.Services.CreateScope().ServiceProvider.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync("C02BOSUILEN");
        await repository().AddAsync(new Domain.Entities.Submission
        {
            GroupId = bosuilen!.Id, AssignmentNumber = 1, FileName = "oud.jpg", StoragePath = earlierPath,
            SlackChannelId = "C02BOSUILEN", SlackMessageId = "1.0"
        });

        var rebuild = _app.Factory.Services.GetRequiredService<ISlackRebuildService>();
        rebuild.IsAvailable.Should().BeTrue();

        // Start the rebuild; the fake Slack holds the first history request open
        rebuild.TryStart(_since).Should().BeTrue();
        await _historyRequested.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A live upload arrives meanwhile: it must wait for the rebuild, not be lost or wiped
        var liveTs = Ts(900);
        var liveEvent = TestApp.SignedSlackRequest(Secret, JsonSerializer.Serialize(new
        {
            type = "event_callback",
            @event = new
            {
                type = "message", channel = "C04POOLVOSSEN", user = "U_SCOUT", text = "Opdracht 8", ts = liveTs,
                files = new[] { new { name = "live.jpg", mimetype = "image/jpeg", size = 3, url_private = "https://files.slack.com/live.jpg" } }
            }
        }));
        var live = client.SendAsync(liveEvent);
        await Task.Delay(300);
        live.IsCompleted.Should().BeFalse("live processing is paused during the rebuild");

        _releaseHistory.SetResult();
        await rebuild.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        (await live.WaitAsync(TimeSpan.FromSeconds(30))).StatusCode.Should().Be(HttpStatusCode.OK);

        var progress = rebuild.Progress;
        progress.State.Should().Be(RebuildState.Completed, progress.Error);
        progress.SubmissionsImported.Should().Be(3);
        progress.ReviewsApplied.Should().Be(1);

        var all = await repository().SearchSubmissionsAsync(new SubmissionFilter());
        all.Select(s => s.FileName).Should().BeEquivalentTo("zee.jpg", "draad.jpg", "antwoord.jpg", "live.jpg");

        var awesome = all.Single(s => s.FileName == "zee.jpg");
        awesome.Status.Should().Be(SubmissionStatus.Awesome);
        awesome.StoragePath.Should().EndWith(Path.Combine("de-zeearenden", "awesome", "DZA 5.jpg"));
        (await File.ReadAllBytesAsync(awesome.StoragePath)).Should().Equal(1, 2, 3);
        all.Single(s => s.FileName == "antwoord.jpg").AssignmentNumber.Should().Be(7, "thread replies are imported too");

        (await File.ReadAllTextAsync(Path.Combine(progress.BackupFolder!, "submissions.json"))).Should().Contain("oud.jpg");
        File.Exists(Path.Combine(progress.BackupFolder!, "de-bosuilen", "DBU 1.jpg")).Should().BeTrue("the earlier file was backed up");
        File.Exists(earlierPath).Should().BeFalse();

        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        (await admin.GetStringAsync("/configuration")).Should().Contain("Rebuild from Slack");
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Just enough of Slack's Web API and file hosting for a rebuild.</summary>
    private sealed class FakeSlack(SlackRebuildIntegrationTests test) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith("https://files.slack.com/"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) };
            }

            if (url.Contains("conversations.history") && url.Contains("channel=C01ZEEARENDEN"))
            {
                test._historyRequested.TrySetResult();
                await test._releaseHistory.Task;
                return Json(new
                {
                    ok = true,
                    messages = new object[]
                    {
                        Message(test.Ts(10), "Opdracht 5", "zee.jpg", reactions: new[] { new { name = "heart", users = new[] { "U_JURY" } } }),
                        new { ts = test.Ts(20), thread_ts = test.Ts(20), reply_count = 1, user = "U_SCOUT", text = "Opdracht 6", files = Files("draad.jpg") }
                    }
                });
            }

            if (url.Contains("conversations.replies"))
            {
                return Json(new
                {
                    ok = true,
                    messages = new object[]
                    {
                        new { ts = test.Ts(20), thread_ts = test.Ts(20), user = "U_SCOUT", text = "Opdracht 6" },
                        new { ts = test.Ts(30), thread_ts = test.Ts(20), user = "U_SCOUT", text = "Opdracht 7", files = Files("antwoord.jpg") }
                    }
                });
            }

            return Json(new { ok = true, messages = Array.Empty<object>() });
        }

        private static object Message(string ts, string text, string file, object reactions) =>
            new { ts, user = "U_SCOUT", text, files = Files(file), reactions };

        private static object[] Files(string name) =>
            new object[] { new { name, mimetype = "image/jpeg", size = 3, url_private_download = $"https://files.slack.com/{name}" } };

        private static HttpResponseMessage Json(object body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }
}
