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
    private readonly string _dbPath;
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly IDictionary<string, string?>? _settings;
    private readonly Action<IServiceCollection>? _services;
    private readonly string _environment;
    private bool _restarted;

    public TestApp(
        WebApplicationFactory<Program> baseFactory,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        string environment = "Development")
        : this(baseFactory, settings, services, environment,
            Path.Combine(Path.GetTempPath(), $"scouting_it_{Guid.NewGuid():N}.db"),
            Path.Combine(Path.GetTempPath(), $"scouting_it_storage_{Guid.NewGuid():N}"))
    {
    }

    private TestApp(
        WebApplicationFactory<Program> baseFactory,
        IDictionary<string, string?>? settings,
        Action<IServiceCollection>? services,
        string environment,
        string dbPath,
        string storageRoot)
    {
        _baseFactory = baseFactory;
        _settings = settings;
        _services = services;
        _environment = environment;
        _dbPath = dbPath;
        StorageRoot = storageRoot;
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

    /// <summary>The signing secret of apps made with <see cref="Live"/>.</summary>
    public const string LiveSecret = "live-test-secret";

    /// <summary>The reviewer of apps made with <see cref="Live"/>.</summary>
    public const string Reviewer = "U_JURY";

    /// <summary>
    /// The app connected to Slack (no simulation), with Slack's Web API and file hosting faked by <paramref name="slack"/>.
    /// Webhook requests must be signed with <see cref="LiveSecret"/>; <see cref="SendEventAsync"/> does that.
    /// </summary>
    public static TestApp Live(
        WebApplicationFactory<Program> baseFactory,
        FakeSlackApi slack,
        IDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var all = new Dictionary<string, string?>
        {
            ["Scouting:Slack:SimulationMode"] = "false",
            ["Scouting:Slack:SigningSecret"] = LiveSecret,
            ["Scouting:Slack:BotToken"] = "xoxb-test",
            ["Scouting:Reviewers:0"] = Reviewer
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            all[key] = value;
        }

        return new TestApp(baseFactory, all, s =>
        {
            s.AddHttpClient<Infrastructure.Services.ISlackClient, Infrastructure.Services.SlackClient>().ConfigurePrimaryHttpMessageHandler(() => slack);
            s.AddHttpClient<Infrastructure.Services.ISlackHistoryClient, Infrastructure.Services.SlackHistoryClient>().ConfigurePrimaryHttpMessageHandler(() => slack);
            services?.Invoke(s);
        });
    }

    /// <summary>
    /// Stops this app and starts it again on the same database, storage and settings files, like a restart of the server.
    /// The returned app owns the files; this one must not be used any more.
    /// </summary>
    public async Task<TestApp> RestartAsync()
    {
        await Factory.DisposeAsync();
        _restarted = true;
        return new TestApp(_baseFactory, _settings, _services, _environment, _dbPath, StorageRoot);
    }

    /// <summary>
    /// Sends a Slack event to the webhook as an event_callback, signed when the app runs live, and returns the app's answer.
    /// </summary>
    public async Task<System.Text.Json.JsonElement> SendEventAsync(HttpClient client, object ev)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { type = "event_callback", @event = ev });
        var live = _settings?.TryGetValue("Scouting:Slack:SigningSecret", out var secret) == true && secret == LiveSecret;
        var response = live
            ? await client.SendAsync(SignedSlackRequest(LiveSecret, body))
            : await client.PostAsync("/api/slack/events", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        var answer = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != System.Net.HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Slack event was answered with {response.StatusCode}: {answer}");
        }

        return System.Text.Json.JsonDocument.Parse(answer).RootElement.Clone();
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
        if (_restarted) return;

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
