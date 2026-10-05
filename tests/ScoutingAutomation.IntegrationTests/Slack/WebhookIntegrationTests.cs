using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// The Slack webhook in simulation mode: request handling and the full review flow over HTTP.
/// </summary>
public class WebhookIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public WebhookIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory, new Dictionary<string, string?>
        {
            ["Scouting:Reviewers:0"] = "U_JURY"
        });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private static object Message(string ts, string text, string file, string mimetype = "image/jpeg", string user = "U_SCOUT") => new
    {
        type = "event_callback",
        @event = new
        {
            type = "message",
            subtype = "file_share",
            channel = "C04POOLVOSSEN",
            user,
            text,
            ts,
            files = new[] { new { name = file, mimetype, size = 10, url_private = $"sim://{file}" } }
        }
    };

    private static object Reaction(string ts, string reaction, string user = "U_JURY") => new
    {
        type = "event_callback",
        @event = new { type = "reaction_added", user, reaction, item = new { type = "message", channel = "C04POOLVOSSEN", ts } }
    };

    private async Task<JsonElement> PostAsync(object envelope)
    {
        var response = await TestApp.PostEventAsync(_client, envelope);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task UploadThenReviewerReaction_ShouldScoreOnDashboard()
    {
        var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.0001";

        var upload = await PostAsync(Message(ts, "Opdracht 12: klaar", "live.jpg"));
        upload.GetProperty("success").GetBoolean().Should().BeTrue();

        var review = await PostAsync(Reaction(ts, "heart"));
        review.GetProperty("success").GetBoolean().Should().BeTrue();

        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync("C04POOLVOSSEN", ts))).Single();
        submission.Status.Should().Be(SubmissionStatus.Awesome);
        submission.AssignmentCode.Should().Be("12");
        var storedName = $"DPV 12 {SlackTimestamp.ToDateTimeOffset(ts)!.Value.ToLocalTime():HH-mm-ss}.jpg";
        Path.GetFileName(submission.StoragePath).Should().Be(storedName);
        File.Exists(submission.StoragePath).Should().BeTrue();
        submission.StoragePath.Should().Contain(Path.Combine("de-poolvossen", "awesome"));

        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        stats.AssignmentStatistics.Single(a => a.AssignmentCode == "12").AwesomeCount.Should().Be(1);

        using var browser = await _app.CreateLoggedInClientAsync();
        var page = await browser.GetStringAsync("/assignments/12");
        page.Should().Contain(storedName);
    }

    [Fact]
    public async Task ReactionFromNonReviewer_ShouldNotChangeStatus()
    {
        var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.0002";
        await PostAsync(Message(ts, "Opdracht 13", "a.jpg"));

        var review = await PostAsync(Reaction(ts, "thumbsup", user: "U_SCOUT"));

        review.GetProperty("success").GetBoolean().Should().BeFalse();
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync("C04POOLVOSSEN", ts)))
            .Single().Status.Should().Be(SubmissionStatus.Pending);
    }

    [Theory]
    [InlineData("hallo", "a.jpg", "image/jpeg", "U_SCOUT")]           // no number
    [InlineData("Opdracht 999", "a.jpg", "image/jpeg", "U_SCOUT")]    // unknown number
    [InlineData("Opdracht 14", "a.zip", "application/zip", "U_SCOUT")] // unsupported file
    [InlineData("Opdracht 14", "a.jpg", "image/jpeg", "U_JURY")]      // reviewer message
    public async Task UnusableMessages_ShouldNotBeStored(string text, string file, string mimetype, string user)
    {
        var ts = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.{Guid.NewGuid().GetHashCode() & 0xffff}";

        var result = await PostAsync(Message(ts, text, file, mimetype, user));

        result.GetProperty("createdSubmissionIds").GetArrayLength().Should().Be(0);
        (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync("C04POOLVOSSEN", ts))).Should().BeEmpty();
    }

    [Fact]
    public async Task IrrelevantEvent_ShouldBeAcknowledgedAsIgnored()
    {
        var result = await PostAsync(new { type = "app_rate_limited" });

        result.GetProperty("status").GetString().Should().Be("ignored");
        result.GetProperty("envelopeType").GetString().Should().Be("app_rate_limited");
    }

    [Theory]
    [InlineData("{not json", "Invalid JSON")]
    [InlineData("null", "Empty payload")]
    public async Task UnreadableBody_ShouldBeBadRequest(string body, string reason)
    {
        var response = await _client.PostAsync("/api/slack/events", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(reason);
    }

    [Fact]
    public async Task OversizedBodyWithLength_ShouldBeRejected()
    {
        var content = new ByteArrayContent(new byte[1024 * 1024 + 1]);

        var response = await _client.PostAsync("/api/slack/events", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task OversizedBodyWithoutLength_ShouldBeRejected()
    {
        var content = new StreamContent(new MemoryStream(new byte[1024 * 1024 + 1]));
        content.Headers.ContentLength = null;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slack/events") { Content = content };
        request.Headers.TransferEncodingChunked = true;

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
