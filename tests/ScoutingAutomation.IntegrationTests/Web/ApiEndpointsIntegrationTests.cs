using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ScoutingAutomation.Contracts.Slack;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

public class ApiEndpointsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly HttpClient _client;
    private readonly string _testDbPath;
    private readonly string _testStorageDir;

    public ApiEndpointsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"scouting_api_test_{Guid.NewGuid():N}.db");
        _testStorageDir = Path.Combine(Path.GetTempPath(), $"scouting_api_storage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testStorageDir);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => TestApp.UseTestSettingsOnly(config));
            builder.UseSetting("Scouting:KeysFile", Path.Combine(Path.GetTempPath(), $"scouting_keys_{Guid.NewGuid():N}.json"));
            builder.UseSetting("Scouting:RuntimeSettingsFile", Path.Combine(Path.GetTempPath(), $"scouting_rt_{Guid.NewGuid():N}.json"));
            builder.UseSetting("UseSqlite", "true");
            builder.UseSetting("ConnectionStrings:scoutingdb", $"Data Source={_testDbPath}");
            builder.UseSetting("Scouting:StorageRoot", _testStorageDir);
            builder.UseSetting("Scouting:Slack:SimulationMode", "true");
        }).CreateClient();
    }

    [Fact]
    public async Task SlackWebhook_UrlVerificationChallenge_ShouldReturnChallengeText()
    {
        // Arrange
        const string expectedChallenge = "test_challenge_abc123xyz";
        var payload = new SlackEventEnvelopeDto
        {
            Type = "url_verification",
            Challenge = expectedChallenge
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/slack/events", payload);
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: content);
        content.Should().Be(expectedChallenge);
    }

    [Fact]
    public async Task SlackWebhook_EventCallback_ShouldReturnOk()
    {
        // Arrange
        var payload = new SlackEventEnvelopeDto
        {
            Type = "event_callback",
            Event = new SlackEventItemDto
            {
                Type = "message",
                Channel = "C01ZEEARENDEN",
                User = "Scout1",
                Text = "Opdracht 1: Foto kampvuur",
                Ts = "1234567.0001",
                Files = new List<SlackFileItemDto>
                {
                    new() { Name = "kampvuur.jpg", Size = 1024, UrlPrivateDownload = "sim://kampvuur.jpg" }
                }
            }
        };

        // Act
        var response = await _client.PostAsJsonAsync("/api/slack/events", payload);
        var body = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK, because: body);
    }

    [Fact]
    public async Task HomePage_WithoutLogin_ShouldRedirectToLogin()
    {
        // Act
        var response = await _client.GetAsync("/");

        // Assert: the default client follows the redirect to the login page
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.Should().Be("/login");
        (await response.Content.ReadAsStringAsync()).Should().Contain("Enter the access key");
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
