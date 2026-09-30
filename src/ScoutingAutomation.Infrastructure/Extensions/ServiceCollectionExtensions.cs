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
    public static IServiceCollection AddScoutingInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ScoutingSettings>(configuration.GetSection(ScoutingSettings.SectionName));

        services.AddScoped<ISubmissionRepository, SubmissionRepository>();
        services.AddScoped<IGroupRepository, GroupRepository>();
        services.AddScoped<IAssignmentRepository, AssignmentRepository>();

        services.AddSingleton<IAssignmentParser, AssignmentParser>();
        services.AddSingleton<IFileStorageService, LocalFileStorageService>();
        services.AddSingleton<ISubmissionChangeNotifier, SubmissionChangeNotifier>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISlackRequestVerifier, SlackRequestVerifier>();
        services.AddScoped<ISlackEventDispatcher, SlackEventDispatcher>();
        services.AddSingleton<SlackConnectionStatus>();
        services.AddHttpClient<ISlackSocketConnector, SlackSocketConnector>();
        services.AddHostedService<SlackSocketModeService>();

        services.AddHttpClient<ISlackClient, SlackClient>();
        services.AddHttpClient<ISlackHistoryClient, SlackHistoryClient>();
        services.AddSingleton<SlackProcessingGate>();
        services.AddSingleton<RuntimeSettingsService>();
        services.AddScoped<ReviewerNameFetcher>();
        services.AddSingleton<AccessKeyService>();
        services.AddSingleton<LoginAttemptLimiter>();
        services.AddSingleton<ISlackRebuildService, SlackRebuildService>();
        services.AddScoped<ISubmissionWorkflowService, SubmissionWorkflowService>();
        services.AddScoped<DatabaseInitializer>();

        return services;
    }
}
