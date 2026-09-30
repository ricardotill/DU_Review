using System.Net;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.UnitTests;

internal static class TestRuntime
{
    /// <summary>Runtime settings for the given options; nothing is read from or written to disk until a reviewer is added.</summary>
    public static ScoutingAutomation.Infrastructure.Services.RuntimeSettingsService For(Microsoft.Extensions.Options.IOptions<ScoutingAutomation.Contracts.Configuration.ScoutingSettings> options) =>
        new(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScoutingAutomation.Infrastructure.Services.RuntimeSettingsService>.Instance);
}

internal static class TestDb
{
    /// <summary>A fresh, isolated in-memory database.</summary>
    public static ScoutingDbContext Create(string? name = null) =>
        new(new DbContextOptionsBuilder<ScoutingDbContext>()
            .UseInMemoryDatabase(name ?? $"Test_{Guid.NewGuid():N}")
            .Options);
}

/// <summary>
/// Records outgoing HTTP requests and answers them with a configurable response.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public static FakeHttpHandler Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request, body));
        return _respond(request);
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ScoutingTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
