using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// The Slack webhook with simulation mode off: only correctly signed requests get through.
/// </summary>
public class SlackSignatureIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private const string Secret = "integration-test-secret";
    private const string ChallengeBody = "{\"type\":\"url_verification\",\"challenge\":\"abc123\"}";

    private readonly HttpClient _client;
    private readonly string _testDbPath;
    private readonly string _testStorageDir;

    public SlackSignatureIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"scouting_sig_test_{Guid.NewGuid():N}.db");
        _testStorageDir = Path.Combine(Path.GetTempPath(), $"scouting_sig_storage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testStorageDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => TestApp.UseTestSettingsOnly(config));
            builder.UseSetting("ConnectionStrings:scoutingdb", $"Data Source={_testDbPath}");
            builder.UseSetting("Scouting:StorageRoot", _testStorageDir);
            builder.UseSetting("Scouting:Slack:SimulationMode", "false");
            builder.UseSetting("Scouting:Slack:SigningSecret", Secret);
        }).CreateClient();
    }

    private static HttpRequestMessage SlackRequest(string body, string? timestamp, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slack/events")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (timestamp != null) request.Headers.Add("X-Slack-Request-Timestamp", timestamp);
        if (signature != null) request.Headers.Add("X-Slack-Signature", signature);
        return request;
    }

    [Fact]
    public async Task SignedRequest_ShouldBeProcessed()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = SlackRequestVerifier.ComputeSignature(Secret, ts, Encoding.UTF8.GetBytes(ChallengeBody));

        var response = await _client.SendAsync(SlackRequest(ChallengeBody, ts, signature));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("abc123");
    }

    [Fact]
    public async Task UnsignedRequest_ShouldBeRejected()
    {
        var response = await _client.SendAsync(SlackRequest(ChallengeBody, null, null));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TamperedRequest_ShouldBeRejected()
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = SlackRequestVerifier.ComputeSignature(Secret, ts, Encoding.UTF8.GetBytes(ChallengeBody));
        var fakeReaction = "{\"type\":\"event_callback\",\"event\":{\"type\":\"reaction_added\",\"user\":\"U_FAKE\",\"reaction\":\"thumbsup\"}}";

        var response = await _client.SendAsync(SlackRequest(fakeReaction, ts, signature));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    public void Dispose()
    {
        _client.Dispose();
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (Directory.Exists(_testStorageDir)) Directory.Delete(_testStorageDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
