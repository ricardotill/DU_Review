using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Extensions;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Web.Adventurers;
using ScoutingAutomation.Web.Auth;
using ScoutingAutomation.Web.Components;
using ScoutingAutomation.Web.Export;
using ScoutingAutomation.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// The app is Dutch: dates and numbers are shown the Dutch way
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("nl-NL");

// Aspire service defaults (OpenTelemetry, HealthChecks, ServiceDiscovery)
builder.AddServiceDefaults();

// Relative paths are taken from the app's folder, not from wherever it was started: otherwise a start from another
// folder silently gets an empty database, new keys and no assignments next to the full storage folder
string FromContentRoot(string path) => Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, path));
foreach (var (key, fallback) in new[]
         {
             ("Scouting:StorageRoot", "./scouting_storage"), ("Scouting:AssignmentPointsFile", "./opdrachtenpunten.csv"),
             ("Scouting:RuntimeSettingsFile", "./runtime-settings.json")
         })
{
    var value = builder.Configuration[key];
    builder.Configuration[key] = FromContentRoot(string.IsNullOrWhiteSpace(value) ? fallback : value);
}

// Add services to the container
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure Database per ADR-001 & AR-002: PostgreSQL via Aspire when running in Aspire, SQLite when standalone
var aspireConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__scoutingdb");
var configConnectionString = builder.Configuration.GetConnectionString("scoutingdb");
var connectionString = !string.IsNullOrWhiteSpace(aspireConnectionString) ? aspireConnectionString : configConnectionString;
var isAspire = !string.IsNullOrWhiteSpace(aspireConnectionString) || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPIRE_RESOURCE_NAME"));

var databaseDescription = "PostgreSQL via Aspire";
if (isAspire && !string.IsNullOrWhiteSpace(connectionString) && !connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase))
{
    // PostgreSQL registered via Aspire component
    builder.AddNpgsqlDbContext<ScoutingDbContext>("scoutingdb");
    builder.AddNpgsqlDbContext<AccountsDbContext>("scoutingdb");
}
else
{
    // SQLite for standalone / direct local development without Aspire Postgres container
    var sqliteConnection = !string.IsNullOrWhiteSpace(connectionString) && connectionString.Contains("Data Source=", StringComparison.OrdinalIgnoreCase)
        ? connectionString
        : "Data Source=scouting_dev.db";

    // A relative database file is in the folder the app is started from; logged in full at startup,
    // so a start from another folder (with an empty database) is easy to spot
    var dataSource = new SqliteConnectionStringBuilder(sqliteConnection).DataSource;
    databaseDescription = $"SQLite {(dataSource == ":memory:" ? dataSource : Path.GetFullPath(dataSource))}";

    builder.Services.AddDbContext<ScoutingDbContext>(options =>
    {
        options.UseSqlite(sqliteConnection);
    });
    builder.Services.AddDbContext<AccountsDbContext>(options => options.UseSqlite(sqliteConnection));
}

// Add Domain & Infrastructure services
builder.Services.AddScoutingInfrastructure(builder.Configuration);
builder.Services.AddScoped<ScoutingAutomation.Web.Favorites.FavoritesService>();
builder.Services.AddScoped<ScoutingAutomation.Web.Notifications.NotificationPreferences>();

// Behind a reverse proxy that handles HTTPS: the login cookie is still marked Secure, and the login limiter sees
// the visitor's address instead of the proxy's. Proxies on this machine are trusted (the default); a proxy elsewhere,
// such as cloudflared in its own container, is added with ForwardedHeaders:KnownNetworks (CIDR, e.g. "172.29.53.10/32").
var knownProxyNetworks = builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [];
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var network in knownProxyNetworks)
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

// Accounts (ASP.NET Identity): admins make every account, there is no registration. See Auth/AppAuth.cs
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(options =>
    {
        options.User.RequireUniqueEmail = true;
        // Long beats complicated: a length, no forced digits or symbols
        options.Password.RequiredLength = 10;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequiredUniqueChars = 4;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AccountsDbContext>()
    .AddErrorDescriber<DutchIdentityErrorDescriber>()
    .AddClaimsPrincipalFactory<AccountClaimsFactory>()
    .AddDefaultTokenProviders();
// Invitation and password links
builder.Services.Configure<DataProtectionTokenProviderOptions>(options => options.TokenLifespan = AccountService.LinkLifetime);
// A blocked account or a changed password ends the login on the next request after this
builder.Services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "du_login";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/login";
    // Reached by a group or the beamer that opens a jury page
    options.AccessDeniedPath = "/geen-toegang";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});
// Every page and endpoint is for the jury, unless marked [GroupAccess] or [BeamerAccess]: see Auth/GroupAccessHandler.cs
builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(AppAuth.Scheme)
        .RequireAuthenticatedUser()
        .AddRequirements(new StaffOrGroupPageRequirement())
        .Build();
});
builder.Services.AddSingleton<IAuthorizationHandler, GroupAccessHandler>();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AuthenticationStateProvider, AccountAuthenticationStateProvider>();

var app = builder.Build();

var scoutingSettings = app.Services.GetRequiredService<IOptions<ScoutingSettings>>().Value;
if (!GameTime.Configure(scoutingSettings.TimeZone))
{
    app.Logger.LogWarning("Unknown time zone '{TimeZone}' in Scouting:TimeZone: using the server's time zone {Local}", scoutingSettings.TimeZone, TimeZoneInfo.Local.Id);
}

app.Logger.LogInformation("Database {Database}; storage {Storage}, settings {Settings}, assignment points {Points}",
    databaseDescription, scoutingSettings.StorageRoot, scoutingSettings.RuntimeSettingsFile, scoutingSettings.AssignmentPointsFile);
if (!scoutingSettings.Email.IsConfigured)
{
    app.Logger.LogWarning("No SMTP server set (Scouting:Email): invitations and password mails are not sent; admins can copy the links instead");
}

if (scoutingSettings.Slack.SimulationMode && !app.Environment.IsDevelopment())
{
    // Simulation does not check Slack's signature: anyone who can reach the app could post fake reviews
    app.Logger.LogCritical("Scouting:Slack:SimulationMode is on outside Development: Slack requests are not verified and nothing reaches Slack");
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
app.UseForwardedHeaders();
app.MapDefaultEndpoints();
app.UseSecurityHeaders();

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
        await scope.ServiceProvider.GetRequiredService<AccountsInitializer>().InitializeAsync();
    }
    catch (StartupConfigurationException ex)
    {
        // Waiting does not fix a wrong setting: stop with a clear message instead of failing every Slack event
        scope.ServiceProvider.GetRequiredService<ILogger<Program>>().LogCritical(ex, "The app cannot start: {Problem}", ex.Message);
        throw;
    }
    catch (Exception ex)
    {
        // The app keeps running (e.g. a database server that is not up yet); its health check and pages show the problem
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogCritical(ex, "Could not prepare the database. Check that it is reachable and that the connection string is right");
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

// FR-001 / Slack Events Webhook Endpoint. With Socket Mode, events come over the app's own connection:
// the public endpoint is then not needed, and not offered.
if (slackSettings.SimulationMode || !slackSettings.SocketMode)
{
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

        // Also for a body without a length (chunked): Kestrel stops reading past the limit instead of buffering 30 MB
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxSlackBodyBytes + 1;
        }

        // The signature covers the exact bytes Slack sent, so read the raw body before parsing
        using var buffer = new MemoryStream();
        try
        {
            await request.Body.CopyToAsync(buffer);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
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
}

// File Preview & Download Endpoint
api.MapGet("/files/{id:guid}", async (
    HttpRequest request,
    Guid id,
    ISubmissionRepository repository,
    IFileStorageService fileStorage) =>
{
    var submission = await repository.GetByIdAsync(id);
    var user = request.HttpContext.User;
    // A group only gets its own files, the beamer no rejected ones (the slideshow skips them); a file it may not
    // have looks the same as one that does not exist
    if (submission == null
        || (AppAuth.GroupId(user) is { } ownGroup && submission.GroupId != ownGroup)
        || (AppAuth.IsBeamer(user) && submission.Status == ScoutingAutomation.Domain.Enums.SubmissionStatus.Rejected))
    {
        return Results.NotFound();
    }

    var stream = await fileStorage.OpenReadStreamAsync(submission.StoragePath);
    if (stream == null)
    {
        return Results.NotFound("File stream not available on disk");
    }

    // The type comes from the file's extension, not from what Slack reported: an uploaded page or script is never
    // served as one. The sandbox policy keeps anything that is opened directly from running scripts in the app.
    var http = request.HttpContext;
    http.Response.Headers.ContentSecurityPolicy = "sandbox; default-src 'none'; img-src 'self' data:; media-src 'self'; style-src 'unsafe-inline'";
    return Results.File(stream, SafeContentType.For(submission.FileName), submission.FileName, enableRangeProcessing: true);
}).RequireAuthorization().WithMetadata(new GroupAccessAttribute(), new BeamerAccessAttribute());

api.MapExportEndpoints();
app.MapPublicMapEndpoints();

app.MapPost("/logout", async (HttpContext http) =>
{
    // The antiforgery middleware only records a failed check; an endpoint that binds no form must act on it itself
    if (http.Features.Get<IAntiforgeryValidationFeature>() is not { IsValid: true })
    {
        return Results.BadRequest();
    }

    await http.SignOutAsync(AppAuth.Scheme);
    // The other cookies AddIdentity sets up (external login, two-factor) are never used; signing out of them is harmless
    await http.SignOutAsync(IdentityConstants.ExternalScheme);
    return Results.Redirect("/login");
}).WithMetadata(new RequireAntiforgeryTokenAttribute());

// Blazor UI
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();

public partial class Program { }
