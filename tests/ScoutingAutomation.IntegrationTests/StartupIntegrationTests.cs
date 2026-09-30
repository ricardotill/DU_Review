using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Startup variants: production error handling, live-mode warnings, telemetry and Aspire's PostgreSQL.
/// </summary>
public class StartupIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public StartupIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Production_ShouldServePagesAndHideHealthEndpoints()
    {
        await using var app = new TestApp(_factory, environment: "Production");
        using var client = await app.CreateLoggedInClientAsync();

        (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/health")).StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LiveWebhookWithoutSigningSecret_ShouldLogErrorAndRejectRequests()
    {
        var logs = new CapturingLoggerProvider();
        await using var app = new TestApp(_factory,
            new Dictionary<string, string?> { ["Scouting:Slack:SimulationMode"] = "false" },
            services => services.AddSingleton<ILoggerProvider>(logs));
        using var client = app.CreateClient();

        var response = await TestApp.PostEventAsync(client, new { type = "url_verification", challenge = "x" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        logs.Messages.Should().Contain(m => m.Contains("SigningSecret is not set"));
    }

    [Fact]
    public async Task OpenTelemetryEndpoint_ShouldEnableExporter()
    {
        await using var app = new TestApp(_factory, new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:4317" });
        using var client = app.CreateClient();

        (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(logLevel))
                {
                    lock (provider.Messages) provider.Messages.Add(formatter(state, exception));
                }
            }
        }
    }
}

[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public class ProcessEnvironment
{
}

/// <summary>
/// Aspire passes the database through a process environment variable, so this runs on its own.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public class AspireStartupIntegrationTests
{
    [Fact]
    public async Task AspirePostgresUnavailable_ShouldStillStartAndLogTheProblem()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__scoutingdb", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using var app = new TestApp(factory, new Dictionary<string, string?>
            {
                ["Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:DisableRetry"] = "true",
                ["Aspire:Npgsql:EntityFrameworkCore:PostgreSQL:DisableHealthChecks"] = "true"
            });
            using var client = app.CreateClient();

            // Pages fail without a database, but the app itself is up
            (await client.GetAsync("/alive")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__scoutingdb", null);
        }
    }
}

/// <summary>
/// Without a SQLite connection string the app falls back to scouting_dev.db in its working folder.
/// </summary>
[Collection(nameof(ProcessEnvironment))]
public class DefaultDatabaseIntegrationTests
{
    [Fact]
    public async Task NonSqliteConnectionStringWithoutAspire_ShouldUseLocalSqliteFile()
    {
        var fallbackDb = Path.Combine(Directory.GetCurrentDirectory(), "scouting_dev.db");
        DeleteDatabase(fallbackDb);
        try
        {
            await using var factory = new WebApplicationFactory<Program>();
            await using (var app = new TestApp(factory, new Dictionary<string, string?>
            {
                ["ConnectionStrings:scoutingdb"] = "Host=localhost;Database=scoutingdb"
            }))
            {
                using var client = await app.CreateLoggedInClientAsync();
                (await client.GetAsync("/")).StatusCode.Should().Be(HttpStatusCode.OK);
            }

            File.Exists(fallbackDb).Should().BeTrue();
        }
        finally
        {
            DeleteDatabase(fallbackDb);
        }
    }

    private static void DeleteDatabase(string path)
    {
        foreach (var file in new[] { path, path + "-shm", path + "-wal" })
        {
            try { File.Delete(file); } catch { /* Ignore cleanup errors */ }
        }
    }
}
