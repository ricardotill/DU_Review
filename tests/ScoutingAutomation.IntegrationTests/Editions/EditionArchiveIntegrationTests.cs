using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Editions;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Editions;

/// <summary>Exporting an edition with its files from Beheer, and uploading such a ZIP again as a new edition.</summary>
public class EditionArchiveIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;

    public EditionArchiveIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private Task<Edition> ActiveAsync() =>
        _app.GetAsync(sp => sp.GetRequiredService<ScoutingDbContext>().Editions.AsNoTracking().SingleAsync(e => e.Status == EditionStatus.Active));

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] zip)
    {
        var html = await client.GetStringAsync("/admin");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(zip), "file", "editie.zip" }
        };
        return await client.PostAsync("/api/editions/import", form);
    }

    [Fact]
    public async Task Export_ShouldBeAZipWithTheDataAndTheFiles_ForAdminsOnly()
    {
        var edition = await ActiveAsync();
        using var jury = await _app.CreateLoggedInClientAsync();
        (await jury.GetAsync($"/api/editions/{edition.Id}/export.zip")).Headers.Location!.OriginalString.Should().Contain("/geen-toegang");

        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        var response = await admin.GetAsync($"/api/editions/{edition.Id}/export.zip");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain(".du-editie.zip");
        await using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
        zip.Entries.Should().Contain(e => e.FullName == EditionArchive.DataEntry);
        zip.Entries.Count(e => e.FullName.StartsWith(EditionArchive.FilesFolder)).Should().BeGreaterThan(0, "the sample data has files");
        (await admin.GetAsync($"/api/editions/{Guid.NewGuid()}/export.zip")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Import_OfAnExport_ShouldAddAnArchivedEditionAndGoBackToBeheer()
    {
        var edition = await ActiveAsync();
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        var zip = await admin.GetByteArrayAsync($"/api/editions/{edition.Id}/export.zip");

        // The same edition is here already
        var refused = await UploadAsync(admin, zip);
        refused.StatusCode.Should().Be(HttpStatusCode.Redirect);
        refused.Headers.Location!.OriginalString.Should().Contain("importfout=");
        (await admin.GetStringAsync(refused.Headers.Location.OriginalString)).Should().Contain($"Er is al een editie {edition.Name}");

        await _app.GetAsync(async sp =>
        {
            var db = sp.GetRequiredService<ScoutingDbContext>();
            (await db.Editions.SingleAsync(e => e.Id == edition.Id)).Name = "Hernoemd";
            return await db.SaveChangesAsync();
        });
        var done = await UploadAsync(admin, zip);

        done.Headers.Location!.OriginalString.Should().Contain("geimporteerd=");
        (await admin.GetStringAsync(done.Headers.Location.OriginalString)).Should().Contain($"{edition.Name} is geïmporteerd");
        var imported = await _app.GetAsync(sp => sp.GetRequiredService<ScoutingDbContext>().Editions.AsNoTracking().SingleAsync(e => e.Name == edition.Name));
        imported.Status.Should().Be(EditionStatus.Archived);
        var files = await _app.GetAsync(sp => sp.GetRequiredService<ScoutingDbContext>().Submissions.IgnoreQueryFilters()
            .Where(s => s.Group!.EditionId == imported.Id).Select(s => s.StoragePath).ToListAsync());
        files.Should().NotBeEmpty().And.OnlyContain(f => f.StartsWith(Path.Combine(_app.StorageRoot, "_edities")) && File.Exists(f));
    }

    [Fact]
    public async Task Import_WithoutAntiforgeryTokenOrAsJury_ShouldBeRefused()
    {
        using var admin = await _app.CreateLoggedInClientAsync(admin: true);
        using var noToken = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "editie.zip" } };
        (await admin.PostAsync("/api/editions/import", noToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        using var jury = await _app.CreateLoggedInClientAsync();
        using var form = new MultipartFormDataContent { { new ByteArrayContent([1]), "file", "editie.zip" } };
        (await jury.PostAsync("/api/editions/import", form)).Headers.Location!.OriginalString.Should().Contain("/geen-toegang");
    }
}
