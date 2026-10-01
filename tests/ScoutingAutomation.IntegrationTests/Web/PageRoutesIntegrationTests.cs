using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

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

        (await GetPageAsync("/")).Should().Contain("Scorebord");
        (await GetPageAsync("/submissions")).Should().Contain(submission.FileName);
        (await GetPageAsync($"/submissions?group={groupId}")).Should().Contain("Inzendingen");
        (await GetPageAsync($"/submissions/{submission.Id}")).Should().Contain("Beoordelingen");
        (await GetPageAsync($"/submissions/{Guid.NewGuid()}")).Should().Contain("Inzending niet gevonden");
        (await GetPageAsync("/slideshow")).Should().Contain("Diavoorstelling starten");
        (await GetPageAsync("/scoreboard")).Should().Contain("board-row");
        (await GetPageAsync("/assignments")).Should().Contain("Opdrachten");
        (await GetPageAsync("/assignments/1")).Should().Contain("Opdracht 1");
        (await GetPageAsync("/assignments/1?open=true")).Should().Contain("Alleen open inzendingen");
        (await GetPageAsync("/queue")).Should().Contain("Te beoordelen");
        (await GetPageAsync("/slideshow?status=Awesome")).Should().Contain("Alleen awesome");
        (await GetPageAsync("/assignments/999")).Should().Contain("Staat niet in de opdrachtenlijst");
        (await GetPageAsync("/groups")).Should().Contain("group-card");
        (await GetPageAsync($"/groups/{groupId}")).Should().Contain("Plaats ");
        (await GetPageAsync($"/groups/{Guid.NewGuid()}")).Should().Contain("Groep niet gevonden");
        (await GetPageAsync("/configuration")).Should().Contain("opdrachtenpunten.csv").And.Contain("Beoordelingsregels");
        (await GetPageAsync("/simulator")).Should().Contain("Bestand uploaden");
        (await GetPageAsync("/events")).Should().Contain("Gebeurtenissen");
        (await GetPageAsync("/not-found")).Should().Contain("Pagina niet gevonden");
        (await GetPageAsync("/Error")).Should().Contain("Er ging iets mis");
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

    [Fact]
    public async Task FileEndpoint_StoragePathOutsideRoot_ShouldReturnNotFound()
    {
        var secret = Path.Combine(Path.GetTempPath(), $"secret_{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(secret, "geheim");
        try
        {
            var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()))).First();
            submission.StoragePath = secret;
            await _app.GetAsync(async sp => { await sp.GetRequiredService<ISubmissionRepository>().UpdateAsync(submission); return true; });

            var response = await _client.GetAsync($"/api/files/{submission.Id}");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("geheim");
        }
        finally
        {
            File.Delete(secret);
        }
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
