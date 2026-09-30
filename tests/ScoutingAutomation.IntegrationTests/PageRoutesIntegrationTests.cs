using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Every page, server-rendered by the real app with its sample data.
/// </summary>
public class PageRoutesIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public PageRoutesIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    public async Task InitializeAsync()
    {
        _client = await _app.CreateLoggedInClientAsync();
    }

    private async Task<string> GetPageAsync(string url)
    {
        var response = await _client.GetAsync(url);
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{url} should render: {html}");
        return html;
    }

    [Fact]
    public async Task AllPages_ShouldRenderWithSampleData()
    {
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).First();
        var groupId = submission.GroupId;

        (await GetPageAsync("/")).Should().Contain("Leaderboard");
        (await GetPageAsync("/submissions")).Should().Contain(submission.FileName);
        (await GetPageAsync($"/submissions?group={groupId}")).Should().Contain("Submissions");
        (await GetPageAsync($"/submissions/{submission.Id}")).Should().Contain("Review history");
        (await GetPageAsync($"/submissions/{Guid.NewGuid()}")).Should().Contain("Submission not found");
        (await GetPageAsync("/assignments")).Should().Contain("Assignments");
        (await GetPageAsync("/assignments/1")).Should().Contain("Assignment #1");
        (await GetPageAsync("/assignments/999")).Should().Contain("Not in the assignment list");
        (await GetPageAsync("/groups")).Should().Contain("group-card");
        (await GetPageAsync($"/groups/{groupId}")).Should().Contain("Rank #");
        (await GetPageAsync($"/groups/{Guid.NewGuid()}")).Should().Contain("Group not found");
        (await GetPageAsync("/configuration")).Should().Contain("opdrachtenpunten.csv");
        (await GetPageAsync("/simulator")).Should().Contain("Upload a file");
        (await GetPageAsync("/not-found")).Should().Contain("Page not found");
        (await GetPageAsync("/Error")).Should().Contain("Something went wrong");
    }

    [Fact]
    public async Task UnknownRoute_ShouldReturnNotFoundPage()
    {
        var response = await _client.GetAsync("/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HealthEndpoints_ShouldAnswerInDevelopment()
    {
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task FileEndpoint_ShouldServeStoredFile()
    {
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .First(s => s.ContentType == "image/jpeg");

        var response = await _client.GetAsync($"/api/files/{submission.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        response.Content.Headers.ContentDisposition!.FileNameStar.Should().Be(submission.FileName);
        (await response.Content.ReadAsByteArrayAsync()).Take(4).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    [Fact]
    public async Task FileEndpoint_UnknownSubmissionOrMissingFile_ShouldReturnNotFound()
    {
        (await _client.GetAsync($"/api/files/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).First();
        File.Delete(submission.StoragePath);

        var response = await _client.GetAsync($"/api/files/{submission.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("not available on disk");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
