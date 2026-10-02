using System.IO.Compression;
using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>The scoreboard as CSV and the approved and awesome files as ZIP, for admins only.</summary>
public class ExportIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly TestApp _app;

    public ExportIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    [Theory]
    [InlineData("/api/export/scores.csv")]
    [InlineData("/api/export/files.zip")]
    public async Task Export_AsJury_ShouldBeRefused(string url)
    {
        using var jury = await _app.CreateLoggedInClientAsync();

        var response = await jury.GetAsync(url);

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        (await _app.CreateClient().GetAsync(url)).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ScoresCsv_ShouldListGroupsByRankForDutchExcel()
    {
        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);

        var response = await admin.GetAsync("/api/export/scores.csv");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).Should().Equal(Encoding.UTF8.GetPreamble(), "Excel needs the BOM to read UTF-8");
        var lines = Encoding.UTF8.GetString(bytes[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("Plaats;Groep;Afkorting;Punten;Avonturiers;Reisbureau;");
        lines.Should().HaveCount(stats.GroupStatistics.Count + 1);
        var leader = stats.GroupStatistics.OrderBy(g => g.Rank).ThenBy(g => g.GroupName).First();
        lines[1].Should().StartWith($"1;{leader.GroupName};").And.Contain($";{leader.Score};");
    }

    [Fact]
    public async Task FilesZip_ShouldHoldApprovedAndAwesomeFilesPerGroup()
    {
        var all = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter()));
        var awesome = all.Where(s => s.Status == SubmissionStatus.Awesome && File.Exists(s.StoragePath)).ToList();
        var approved = all.Where(s => s.Status == SubmissionStatus.Approved && File.Exists(s.StoragePath)).ToList();
        awesome.Should().NotBeEmpty("the sample data has awesome submissions");
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);

        var awesomeZip = await ReadZipAsync(admin, "/api/export/files.zip?status=awesome");
        var approvedZip = await ReadZipAsync(admin, "/api/export/files.zip");

        awesomeZip.Should().HaveCount(awesome.Count).And.OnlyContain(e => e.Split('/')[1] == "awesome");
        awesomeZip.Should().Contain($"{awesome[0].Group!.Name}/awesome/{Path.GetFileName(awesome[0].StoragePath)}");
        approvedZip.Should().HaveCount(awesome.Count + approved.Count);

        var group = awesome[0].GroupId;
        (await ReadZipAsync(admin, $"/api/export/files.zip?group={group}"))
            .Should().OnlyContain(e => e.StartsWith(awesome[0].Group!.Name + "/"));
        (await admin.GetAsync("/api/export/files.zip?status=rejected")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<List<string>> ReadZipAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/zip");
        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        return zip.Entries.Select(e => e.FullName).ToList();
    }
}
