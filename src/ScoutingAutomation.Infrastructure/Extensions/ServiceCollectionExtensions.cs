using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Cluster;
using ScoutingAutomation.Infrastructure.Editions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Access;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Announcements;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Slack.SocketMode;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Infrastructure.Storage;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Outer limit for one Slack call. SlackClient sets shorter limits itself: per Web API call, and per read of a
    /// download, so a large video over a slow network still gets through while a stalled one is given up.
    /// </summary>
    public static readonly TimeSpan SlackClientTimeout = TimeSpan.FromMinutes(30);

    public static IServiceCollection AddScoutingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScoutingSettings>(configuration.GetSection(ScoutingSettings.SectionName));

        // One instance unless AddPostgresCluster registered the PostgreSQL parts first
        services.TryAddSingleton<IClusterLocks>(SingleInstance.Instance);
        services.TryAddSingleton<IClusterLeadership>(SingleInstance.Instance);
        services.TryAddSingleton<IClusterBus>(SingleInstance.Instance);
        services.AddSingleton<ActiveEditionRefresher>();
        services.AddHostedService<ClusterSync>();

        services.AddScoped<ISubmissionRepository, SubmissionRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IAssignmentRepository, AssignmentRepository>();
        services.AddScoped<IFailedSlackEventRepository, FailedSlackEventRepository>();
        services.AddScoped<SlackEventRetryService>();
        services.AddSingleton<SlackRetryLock>();

        services.AddSingleton<IAssignmentParser, AssignmentParser>();
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();
        services.AddSingleton<ISubmissionChangeNotifier, SubmissionChangeNotifier>();
        services.AddSingleton<SlackEventLog>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider, EventLogLoggerProvider>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISlackRequestVerifier, SlackRequestVerifier>();
        services.AddScoped<ISlackEventDispatcher, SlackEventDispatcher>();
        services.AddSingleton<SlackConnectionStatus>();
        services.AddHttpClient<ISlackSocketConnector, SlackSocketConnector>();
        services.AddSingleton<SlackSocketModeService>();
        services.AddLeaderOnlyHostedService<SlackSocketModeService>();
        services.AddSingleton<SlackCatchUpService>();
        services.AddSingleton<ISlackCatchUpService>(sp => sp.GetRequiredService<SlackCatchUpService>());
        services.AddLeaderOnlyHostedService<SlackCatchUpService>();
        services.AddSingleton<SlackLinks>();
        services.AddSingleton<GameStartAnnouncer>();
        services.AddLeaderOnlyHostedService<GameStartAnnouncer>();
        services.AddSingleton<GameEndAnnouncer>();
        services.AddLeaderOnlyHostedService<GameEndAnnouncer>();
        services.AddSingleton<ReviewReminderService>();
        services.AddLeaderOnlyHostedService<ReviewReminderService>();

        // Editions: the one being played, and making and switching editions
        services.AddSingleton<ScoutingAutomation.Infrastructure.Editions.ActiveEdition>();
        services.AddScoped<ScoutingAutomation.Infrastructure.Editions.EditionService>();
        services.AddScoped<ScoutingAutomation.Infrastructure.Editions.EditionArchive>();

        // Avonturiers: check-ins, theme rounds, broadcasts and the maps
        services.AddScoped<ILocationRepository, LocationRepository>();
        services.AddScoped<LocationService>();
        services.AddScoped<AdventurersScoreService>();
        services.AddSingleton<UnlockNotifier>();
        services.AddHostedService(sp => sp.GetRequiredService<UnlockNotifier>());
        services.AddSingleton<ThemeRoundAnnouncer>();
        services.AddLeaderOnlyHostedService<ThemeRoundAnnouncer>();
        services.AddSingleton<AdventurersStartAnnouncer>();
        services.AddLeaderOnlyHostedService<AdventurersStartAnnouncer>();
        services.AddSingleton<AdventurersFinalAnnouncer>();
        services.AddLeaderOnlyHostedService<AdventurersFinalAnnouncer>();

        // Not the app-wide resilience handler: its 10 s attempt timeout breaks large video downloads,
        // and its retries could post a thread reply twice. SlackClient retries downloads itself.
        // RemoveAllResilienceHandlers is marked experimental, but is the documented way to opt one client out.
#pragma warning disable EXTEXP0001
        services.AddHttpClient<ISlackClient, SlackClient>(http => http.Timeout = SlackClientTimeout)
            .RemoveAllResilienceHandlers();
        // Also not the app-wide handler: its 30 s total and circuit breaker end a whole catch-up on one slow channel.
        // SlackHistoryClient retries reads itself, waiting as long as Slack's rate limit asks.
        services.AddHttpClient<ISlackHistoryClient, SlackHistoryClient>()
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        services.AddSingleton<SlackProcessingGate>();
        services.AddSingleton<SlackChannelLocks>();
        services.AddSingleton<SlackOwnUploads>();
        services.AddSingleton<IRuntimeSettingsStore, DatabaseRuntimeSettingsStore>();
        services.AddSingleton<RuntimeSettingsService>();
        services.AddScoped<ReviewerNameFetcher>();
        services.AddScoped<GroupMessageSender>();
        services.AddSingleton<AccountChangeNotifier>();
        services.AddSingleton<IAccountMailer, SmtpAccountMailer>();
        services.AddScoped<AccountService>();
        services.AddScoped<AccountsInitializer>();
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddSingleton<ISlackRebuildService, SlackRebuildService>();
        services.AddScoped<ISubmissionWorkflowService, SubmissionWorkflowService>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }

    /// <summary>
    /// Lets several instances of the app share one PostgreSQL database: locks, the leader and messages between the
    /// instances go through PostgreSQL. Call before <see cref="AddScoutingInfrastructure"/>.
    /// </summary>
    public static IServiceCollection AddPostgresCluster(this IServiceCollection services, string connectionString)
    {
        services.AddSingleton(_ => new PostgresClusterConnections(connectionString));
        services.AddSingleton<IClusterLocks, PostgresClusterLocks>();
        services.AddSingleton<PostgresLeadership>();
        services.AddSingleton<IClusterLeadership>(sp => sp.GetRequiredService<PostgresLeadership>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgresLeadership>());
        services.AddSingleton<PostgresClusterBus>();
        services.AddSingleton<IClusterBus>(sp => sp.GetRequiredService<PostgresClusterBus>());
        services.AddHostedService(sp => sp.GetRequiredService<PostgresClusterBus>());
        return services;
    }

    /// <summary>Runs the singleton <typeparamref name="T"/> as a hosted service on the leader only.</summary>
    private static void AddLeaderOnlyHostedService<T>(this IServiceCollection services) where T : class, IHostedService =>
        services.AddHostedService(sp => new LeaderOnlyHostedService<T>(sp.GetRequiredService<T>(),
            sp.GetRequiredService<IClusterLeadership>(), sp.GetRequiredService<ILogger<LeaderOnlyHostedService<T>>>()));
}
