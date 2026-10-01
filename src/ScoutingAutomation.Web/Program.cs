using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Extensions;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Auth;
using ScoutingAutomation.Web.Components;
using ScoutingAutomation.Web.Export;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;

var builder = WebApplication.CreateBuilder(args);

// The app is Dutch: dates and numbers are shown the Dutch way
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("nl-NL");

// Aspire service defaults (OpenTelemetry, HealthChecks, ServiceDiscovery)
builder.AddServiceDefaults();

// Add services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure Database per ADR-001 & AR-002: PostgreSQL via Aspire when running in Aspire, SQLite when standalone
var aspireConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__scoutingdb");
var configConnectionString = builder.Configuration.GetConnectionString("scoutingdb");
var connectionString = !string.IsNullOrWhiteSpace(aspireConnectionString) ? aspireConnectionString : configConnectionString;
var isAspire = !string.IsNullOrWhiteSpace(aspireConnectionString) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIRE_RESOURCE_NAME"));

if (isAspire && !string.IsNullOrWhiteSpace(connectionString) && !connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    // PostgreSQL registered via Aspire component
    builder.AddNpgsqlDbContext<ScoutingDbContext>("scoutingdb");
}
else
{
    // SQLite for standalone / direct local development without Aspire Postgres container
    var sqliteConnection = !string.IsNullOrWhiteSpace(connectionString) && connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : "Data Source=scouting_dev.db";

    builder.Services.AddDbContext<ScoutingDbContext>(options =>
    {
        options.UseSqlite(sqliteConnection);
    });
}

// Add Domain & Infrastructure services
builder.Services.AddScoutingInfrastructure(builder.Configuration);
builder.Services.AddScoped<ScoutingAutomation.Web.Favorites.FavoritesService>();
builder.Services.AddScoped<ScoutingAutomation.Web.Notifications.NotificationPreferences>();

// One shared access key, plus an admin key: see Auth/KeyAuth.cs
builder.Services.AddAuthentication(KeyAuth.Scheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "du_login";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/admin";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = KeyAuth.ValidatePrincipalAsync;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, KeyAuthenticationStateProvider>();

var app = builder.Build();

var scoutingSettings = app.Services.GetRequiredService<IOptions<ScoutingSettings>>().Value;
if (!GameTime.Configure(scoutingSettings.TimeZone))
{
    app.Logger.LogWarning("Unknown time zone '{TimeZone}' in Scouting:TimeZone: using the server's time zone {Local}", scoutingSettings.TimeZone, TimeZoneInfo.Local.Id);
}

var runtimeSettings = app.Services.GetRequiredService<RuntimeSettingsService>();
if (runtimeSettings.EventStart is { } eventStart)
{
    app.Logger.LogInformation("Slack messages before the event start {EventStart} are ignored", eventStart);
}

if (runtimeSettings.EventEnd is { } eventEnd)
{
    app.Logger.LogInformation("Submissions from the event end {EventEnd} on do not count", eventEnd);
}

// Configure the HTTP request pipeline
app.MapDefaultEndpoints();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

// Initialize database schema and synchronize groups on startup
using (var scope = app.Services.CreateScope())
{
    try
    {
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.InitializeAsync();
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Failed to initialize database. If Postgres is unavailable, set 'UseSqlite': 'true' in appsettings.");
    }
}

// REST Endpoints for Slack Event Subscriptions & File downloads
var api = app.MapGroup("/api");

// Slack event payloads are small; anything bigger is not from Slack
const int MaxSlackBodyBytes = 1024 * 1024;

var slackSettings = scoutingSettings.Slack;
if (!slackSettings.SimulationMode && !slackSettings.SocketMode && string.IsNullOrWhiteSpace(slackSettings.SigningSecret))
{
    app.Logger.LogError("Scouting:Slack:SigningSecret is not set while SimulationMode is off: all Slack requests will be rejected");
}

// FR-001 / Slack Events Webhook Endpoint
api.MapPost("/slack/events", async (
    HttpRequest request,
    ISlackRequestVerifier verifier,
    ISlackEventDispatcher dispatcher,
    ILogger<Program> logger) =>
{
    if (request.ContentLength > MaxSlackBodyBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    // The signature covers the exact bytes Slack sent, so read the raw body before parsing
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    if (buffer.Length > MaxSlackBodyBytes)
    {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }
    var body = buffer.ToArray();

    var verification = verifier.Verify(
        request.Headers["X-Slack-Request-Timestamp"],
        request.Headers["X-Slack-Signature"],
        body);
    if (!verification.IsAllowed())
    {
        logger.LogWarning("Rejected Slack request from {RemoteIp}: {Reason}", request.HttpContext.Connection.RemoteIpAddress, verification);
        return Results.Unauthorized();
    }

    SlackEventEnvelopeDto? envelope;
    try
    {
        envelope = JsonSerializer.Deserialize<SlackEventEnvelopeDto>(body, JsonSerializerOptions.Web);
    }
    catch (JsonException)
    {
        return Results.BadRequest("Invalid JSON");
    }

    if (envelope == null)
    {
        return Results.BadRequest("Empty payload");
    }

    // 1. URL Verification Challenge from Slack
    if (envelope.Type == "url_verification" && !string.IsNullOrWhiteSpace(envelope.Challenge))
    {
        logger.LogInformation("Responding to Slack url_verification challenge");
        return Results.Text(envelope.Challenge, "text/plain");
    }

    // 2. Event Callback
    // Not tied to the request: Slack may stop waiting after 3 seconds, but a half-processed upload is worse
    var result = await dispatcher.DispatchAsync(envelope, CancellationToken.None);
    if (result != null)
    {
        return Results.Ok(result);
    }

    return Results.Ok(new { status = "ignored", envelopeType = envelope.Type });
});

// File Preview & Download Endpoint
api.MapGet("/files/{id:guid}", async (
    Guid id,
    ISubmissionRepository repository,
    IFileStorageService fileStorage) =>
{
    var submission = await repository.GetByIdAsync(id);
    if (submission == null)
    {
        return Results.NotFound();
    }

    var stream = await fileStorage.OpenReadStreamAsync(submission.StoragePath);
    if (stream == null)
    {
        return Results.NotFound("File stream not available on disk");
    }

    // Range requests let browsers seek in videos and audio
    return Results.File(stream, submission.ContentType, submission.FileName, enableRangeProcessing: true);
}).RequireAuthorization();

api.MapExportEndpoints();

app.MapPost("/logout", async (HttpContext http) =>
{
    // The antiforgery middleware only records a failed check; an endpoint that binds no form must act on it itself
    if (http.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
    {
        return Results.BadRequest();
    }

    await http.SignOutAsync(KeyAuth.Scheme);
    return Results.Redirect("/login");
}).WithMetadata(new RequireAntiforgeryTokenAttribute());

// Blazor UI
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

public partial class Program { }
