using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>Security headers, served file types, request size limits, exports with missing files, and settings the app cannot start with.</summary>
public class HardeningIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly TestApp _app;

    public HardeningIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _app = new TestApp(factory);
    }

    [Fact]
    public async Task Pages_ShouldNotBeShownInsideAnotherSite()
    {
        using var client = await _app.CreateLoggedInClientAsync(admin: true);

        var response = await client.GetAsync("/admin");

        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        // Blazor adds its own policy too; a browser enforces every policy, so the strictest one counts
        response.Headers.GetValues("Content-Security-Policy").Should().Contain(p => p.Contains("frame-ancestors 'none'"));
        response.Headers.GetValues("Referrer-Policy").Should().Equal("same-origin");
    }

    [Fact]
    public async Task Pages_ShouldNotLoadFontsFromGoogle()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        var html = await client.GetStringAsync("/");

        html.Should().NotContain("fonts.googleapis.com");
    }

    [Fact]
    public async Task SubmissionFile_ShouldGetItsTypeFromTheExtensionAndRunNothing()
    {
        var submission = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .First(s => File.Exists(s.StoragePath) && s.StoragePath.EndsWith(".jpg"));
        // What the uploading phone claimed: never passed on
        await _app.GetAsync(async sp =>
        {
            var repository = sp.GetRequiredService<ISubmissionRepository>();
            var stored = (await repository.GetByIdAsync(submission.Id))!;
            stored.ContentType = "text/html";
            await repository.UpdateAsync(stored);
            return 0;
        });
        using var client = await _app.CreateLoggedInClientAsync();

        var response = await client.GetAsync($"/api/files/{submission.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        response.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("sandbox");
    }

    [Fact]
    public async Task SlackWebhook_WithABodyOverTheLimitWithoutALength_ShouldBeRefusedWithoutReadingItAll()
    {
        using var client = _app.CreateClient();
        // A stream without a known length is sent in chunks, so the length check in the header cannot catch it
        var content = new StreamContent(new UnknownLengthStream(2 * 1024 * 1024));

        var response = await client.PostAsync("/api/slack/events", content);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task SlackWebhook_WithSocketMode_ShouldNotBeOffered()
    {
        await using var app = new TestApp(_factory, new Dictionary<string, string?>
        {
            ["Scouting:Slack:SimulationMode"] = "false",
            ["Scouting:Slack:SocketMode"] = "true",
            ["Scouting:Slack:SigningSecret"] = "secret"
        });
        using var client = app.CreateClient();

        var response = await TestApp.PostEventAsync(client, new { type = "url_verification", challenge = "x" });

        response.StatusCode.Should().NotBe(HttpStatusCode.OK, "events come over the app's own connection; the public endpoint only adds risk");
    }

    [Fact]
    public async Task FilesZip_WithAFileGoneFromDisk_ShouldListItInTheDownload()
    {
        var approved = (await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .First(s => s.Status is SubmissionStatus.Approved or SubmissionStatus.Awesome && File.Exists(s.StoragePath));
        File.Delete(approved.StoragePath);
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);

        var response = await admin.GetAsync("/api/export/files.zip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync());
        var list = zip.GetEntry("ONTBREKENDE BESTANDEN.txt");
        list.Should().NotBeNull();
        using var reader = new StreamReader(list!.Open());
        (await reader.ReadToEndAsync()).Should().Contain(Path.GetFileName(approved.StoragePath));
    }

    [Fact]
    public async Task Startup_WithABrokenPointsFile_ShouldStopWithAClearMessage()
    {
        var points = Path.Combine(Path.GetTempPath(), $"punten_{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(points, "Opdracht,standaard,awesome\n1,1,twee\n");
        try
        {
            await using var app = new TestApp(_factory, new Dictionary<string, string?> { ["Scouting:AssignmentPointsFile"] = points });

            var start = () => app.CreateClient();

            start.Should().Throw<StartupConfigurationException>().WithMessage("*punten*line 2*");
        }
        finally
        {
            File.Delete(points);
        }
    }

    [Fact]
    public async Task Startup_WithTwoGroupsInOneFolder_ShouldStopWithAClearMessage()
    {
        await using var app = new TestApp(_factory, new Dictionary<string, string?>
        {
            ["Scouting:Groups:0:Name"] = "De Wolven",
            ["Scouting:Groups:1:Name"] = "de wolven",
            ["Scouting:Groups:1:SlackChannelId"] = "C99OTHER"
        });

        var start = () => app.CreateClient();

        start.Should().Throw<StartupConfigurationException>().WithMessage("*dezelfde map*");
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>A body whose length is not known in advance.</summary>
    private sealed class UnknownLengthStream(long size) : Stream
    {
        private long _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, size - _sent);
            Array.Fill(buffer, (byte)'a', offset, n);
            _sent += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
