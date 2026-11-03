using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Editions;
using ScoutingAutomation.Infrastructure.Slack.SocketMode;

namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>Reads the active edition from the database into <see cref="ActiveEdition"/>, after another instance switched it.</summary>
public sealed class ActiveEditionRefresher(IServiceScopeFactory scopes, ActiveEdition active, ILogger<ActiveEditionRefresher> logger)
{
    /// <summary>The message the other instances get when the active edition changed.</summary>
    public const string ClusterTopic = "edition";

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScoutingDbContext>();
        var edition = await db.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Status == EditionStatus.Active, ct);
        if (edition != null) active.Set(edition);
    }

    /// <summary>As <see cref="RefreshAsync"/>, for event handlers: a failure is logged.</summary>
    public async Task TryRefreshAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read the active edition after a change on another instance");
        }
    }
}

/// <summary>Keeps what this instance holds in memory in line with the other instances.</summary>
public sealed class ClusterSync(
    IClusterBus bus,
    IClusterLeadership leadership,
    ActiveEditionRefresher refresher,
    SlackConnectionStatus slackStatus,
    IOptions<ScoutingSettings> options) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        bus.Subscribe(ActiveEditionRefresher.ClusterTopic, payload => _ = refresher.TryRefreshAsync());
        bus.Reconnected += () => _ = refresher.TryRefreshAsync();
        leadership.Changed += ShowSlackStandby;
        ShowSlackStandby();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        leadership.Changed -= ShowSlackStandby;
        return Task.CompletedTask;
    }

    /// <summary>Only the leader connects to Slack; the others say so instead of showing a missing connection.</summary>
    private void ShowSlackStandby()
    {
        var slack = options.Value.Slack;
        if (!leadership.IsLeader && slack.SocketMode && !slack.SimulationMode)
        {
            slackStatus.Set(SlackConnectionState.Standby);
        }
    }
}
