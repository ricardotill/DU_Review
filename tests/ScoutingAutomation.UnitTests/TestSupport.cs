using System.Net;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.UnitTests;

internal static class TestRuntime
{
    /// <summary>
    /// Runtime settings for the given options. With a full RuntimeSettingsFile path they are kept in that file, so a test restarts the
    /// service by making a new one on the same file, the way the app reads them back from its database.
    /// </summary>
    public static ScoutingAutomation.Infrastructure.Settings.RuntimeSettingsService For(Microsoft.Extensions.Options.IOptions<ScoutingAutomation.Contracts.Configuration.ScoutingSettings> options) =>
        new(options, Microsoft.Extensions.Logging.Abstractions.NullLogger<ScoutingAutomation.Infrastructure.Settings.RuntimeSettingsService>.Instance,
            // Only a path a test chose (in its own temp folder); the default relative one would be shared by all tests
            Path.IsPathRooted(options.Value.RuntimeSettingsFile) ? new FileSettingsStore(options.Value.RuntimeSettingsFile) : null);

    /// <summary>Runtime settings kept in <paramref name="store"/>; two services on one store are two instances of the app.</summary>
    public static ScoutingAutomation.Infrastructure.Settings.RuntimeSettingsService Create(ScoutingAutomation.Contracts.Configuration.ScoutingSettings settings,
        ScoutingAutomation.Infrastructure.Settings.IRuntimeSettingsStore store) =>
        new(Microsoft.Extensions.Options.Options.Create(settings),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ScoutingAutomation.Infrastructure.Settings.RuntimeSettingsService>.Instance, store);
}

/// <summary>Keeps the runtime settings in a file: the stand-in for the database row in unit tests.</summary>
internal sealed class FileSettingsStore(string path) : ScoutingAutomation.Infrastructure.Settings.IRuntimeSettingsStore
{
    private long _version = File.Exists(path) ? 1 : 0;

    public (string? Json, long Version) Load() => File.Exists(path) ? (File.ReadAllText(path), _version) : (null, 0);

    public long? TrySave(string json, long expectedVersion)
    {
        if (expectedVersion != (File.Exists(path) ? _version : 0)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return _version = expectedVersion + 1;
    }
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
