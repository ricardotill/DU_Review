using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// The real app with its own temporary database, storage and settings.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"scouting_it_{Guid.NewGuid():N}.db");

    public TestApp(
        WebApplicationFactory<Program> baseFactory,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        string environment = "Development")
    {
        StorageRoot = Path.Combine(Path.GetTempPath(), $"scouting_it_storage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(StorageRoot);

        Factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, config) => UseTestSettingsOnly(config));
            builder.UseSetting("ConnectionStrings:scoutingdb", $"Data Source={_dbPath}");
            builder.UseSetting("Scouting:StorageRoot", StorageRoot);
            builder.UseSetting("Scouting:AssignmentPointsFile", PointsFile);
            builder.UseSetting("Scouting:RuntimeSettingsFile", Path.Combine(StorageRoot, "runtime-settings.json"));
            builder.UseSetting("Scouting:KeysFile", Path.Combine(StorageRoot, "access-keys.json"));
            builder.UseSetting("Scouting:Slack:SimulationMode", "true");
            foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
            {
                builder.UseSetting(key, value);
            }

            if (services != null)
            {
                builder.ConfigureTestServices(services);
            }
        });
    }

    /// <summary>
    /// Replaces the app's appsettings files and user secrets with testsettings.json. The developer's own
    /// groups, channels and tokens must never influence a test, or let a test reach the real Slack.
    /// Settings passed to the constructor still apply on top.
    /// </summary>
    public static void UseTestSettingsOnly(IConfigurationBuilder config)
    {
        var testSettings = new JsonConfigurationSource
        {
            Path = "testsettings.json",
            FileProvider = new PhysicalFileProvider(AppContext.BaseDirectory),
            Optional = false,
            ReloadOnChange = false
        };

        var appSettingsIndex = -1;
        for (var i = config.Sources.Count - 1; i >= 0; i--)
        {
            if (config.Sources[i] is not JsonConfigurationSource json) continue;

            // appsettings.json, appsettings.{Environment}.json and the user secrets file
            if (json.Path == "appsettings.json") appSettingsIndex = i;
            config.Sources.RemoveAt(i);
        }

        // Same position as appsettings.json, so later sources (such as test overrides) still win
        config.Sources.Insert(appSettingsIndex >= 0 ? appSettingsIndex : 0, testSettings);
    }

    public static string PointsFile { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ScoutingAutomation.Web/opdrachtenpunten.csv"));

    public WebApplicationFactory<Program> Factory { get; }

    public string StorageRoot { get; }

    public const string AccessKey = "TEST-ACCESS-KEY";
    public const string AdminKey = "TEST-ADMIN-KEY";

    public HttpClient CreateClient() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>A client logged in the way a browser does: through the login form.</summary>
    public async Task<HttpClient> CreateLoggedInClientAsync(bool admin = false)
    {
        var client = CreateClient();
        var response = await PostFormAsync(client, "/login", "login", new() { ["Input.Key"] = admin ? AdminKey : AccessKey });
        if (response.StatusCode != System.Net.HttpStatusCode.Redirect)
        {
            throw new InvalidOperationException($"Login failed: {response.StatusCode}");
        }

        return client;
    }

    /// <summary>Opens a page and posts one of its forms, with the page's antiforgery token.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string page, string formName, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(page);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        fields["__RequestVerificationToken"] = token;
        fields["_handler"] = formName;
        return await client.PostAsync(page, new FormUrlEncodedContent(fields));
    }

    public T Get<T>(Func<IServiceProvider, T> query)
    {
        using var scope = Factory.Services.CreateScope();
        return query(scope.ServiceProvider);
    }

    public async Task<T> GetAsync<T>(Func<IServiceProvider, Task<T>> query)
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider);
    }

    public static Task<HttpResponseMessage> PostEventAsync(HttpClient client, object envelope) =>
        client.PostAsJsonAsync("/api/slack/events", envelope);

    /// <summary>A webhook request signed the way Slack signs it, for apps running in live mode.</summary>
    public static HttpRequestMessage SignedSlackRequest(string signingSecret, string body)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/slack/events")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Slack-Request-Timestamp", ts);
        request.Headers.Add("X-Slack-Signature",
            Infrastructure.Services.SlackRequestVerifier.ComputeSignature(signingSecret, ts, System.Text.Encoding.UTF8.GetBytes(body)));
        return request;
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        TryDelete(() => File.Delete(_dbPath));
        TryDelete(() => Directory.Delete(StorageRoot, recursive: true));
    }

    private static void TryDelete(Action delete)
    {
        try
        {
            delete();
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
