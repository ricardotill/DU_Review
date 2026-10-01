using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Interfaces;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// The HTTP endpoints outside the pages: the Slack webhook, file downloads and logging out.
/// </summary>
public class ApiEndpointsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public ApiEndpointsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SlackWebhook_UrlVerificationChallenge_ShouldReturnChallengeText()
    {
        const string expectedChallenge = "test_challenge_abc123xyz";
        var payload = new SlackEventEnvelopeDto { Type = "url_verification", Challenge = expectedChallenge };

        var response = await _client.PostAsJsonAsync("/api/slack/events", payload);
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, because: content);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
        content.Should().Be(expectedChallenge);
    }

    [Fact]
    public async Task SlackWebhook_EventCallback_ShouldShowInTheEventLog()
    {
        var ts = SlackEvents.Ts();

        var result = await _app.SendEventAsync(_client, SlackEvents.Message("C01ZEEARENDEN", ts, "Opdracht 1: Foto kampvuur", SlackEvents.File("kampvuur.jpg")));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync("/events")).Should().Contain("Opdracht 1: Foto kampvuur").And.Contain("1 inzending(en) geregistreerd");
    }

    [Fact]
    public async Task HomePage_WithoutLogin_ShouldRedirectToLogin()
    {
        var response = await _client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.Should().Be("/login");
    }

    [Fact]
    public async Task FileEndpoint_RangeRequest_ShouldReturnPartOfTheFile()
    {
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new Domain.Models.SubmissionFilter())))
            .First(s => s.ContentType == "image/jpeg");
        var length = new FileInfo(submission.StoragePath).Length;
        using var browser = await _app.CreateLoggedInClientAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/files/{submission.Id}");
        request.Headers.Range = new RangeHeaderValue(1, 3);

        var response = await browser.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.PartialContent, "browsers seek in videos with range requests");
        response.Content.Headers.ContentRange!.ToString().Should().Be($"bytes 1-3/{length}");
        (await response.Content.ReadAsByteArrayAsync()).Should().Equal(0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task Logout_WithoutAntiforgeryToken_ShouldBeRefused()
    {
        using var browser = await _app.CreateLoggedInClientAsync();

        var response = await browser.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "another site must not be able to log someone out");
        (await browser.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK, "still logged in");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
