using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Services;

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
        services.AddHostedService<SlackSocketModeService>();
        services.AddSingleton<SlackCatchUpService>();
        services.AddSingleton<ISlackCatchUpService>(sp => sp.GetRequiredService<SlackCatchUpService>());
        services.AddHostedService(sp => sp.GetRequiredService<SlackCatchUpService>());
        services.AddSingleton<SlackLinks>();
        services.AddSingleton<GameStartAnnouncer>();
        services.AddHostedService(sp => sp.GetRequiredService<GameStartAnnouncer>());
        services.AddSingleton<GameEndAnnouncer>();
        services.AddHostedService(sp => sp.GetRequiredService<GameEndAnnouncer>());
        services.AddSingleton<ReviewReminderService>();
        services.AddHostedService(sp => sp.GetRequiredService<ReviewReminderService>());

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
        services.AddSingleton<RuntimeSettingsService>();
        services.AddScoped<ReviewerNameFetcher>();
        services.AddScoped<GroupMessageSender>();
        services.AddSingleton<AccessKeyService>();
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddSingleton<ISlackRebuildService, SlackRebuildService>();
        services.AddScoped<ISubmissionWorkflowService, SubmissionWorkflowService>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
