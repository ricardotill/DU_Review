using System.Threading.Channels;
using ScoutingAutomation.Infrastructure.Cluster;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>
/// Tells a group's Toeristen how many assignments their Avonturiers unlocked. The workflow only queues the group while
/// it holds the Avonturiers channel's lock; the count is read and posted here afterwards, so two channel locks are
/// never held at once. Each Toeristen channel hears a number once: a change back and forth before the post is quiet.
/// Catch-up and rebuild do not queue anything, so Slack never gets an old notice twice.
/// </summary>
public sealed class UnlockNotifier : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettingsService _runtime;
    private readonly ILogger<UnlockNotifier> _logger;
    private readonly IClusterLocks _cluster;
    private int _pending;

    public UnlockNotifier(IServiceScopeFactory scopeFactory, RuntimeSettingsService runtime, ILogger<UnlockNotifier> logger, IClusterLocks? cluster = null)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _logger = logger;
        _cluster = cluster ?? SingleInstance.Instance;
    }

    /// <summary>Groups queued and not handled yet; for tests and the events page.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Queues a check of the group's unlock count after a live change to one of its check-ins.</summary>
    public void Enqueue(Guid groupId)
    {
        if (!_runtime.AdventurersEnabled) return;

        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(groupId)) Interlocked.Decrement(ref _pending);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var groupId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await NotifyAsync(groupId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // One failed notice must not stop the next ones; the next change of the group tries again
                    _logger.LogError(ex, "Could not tell the Toeristen of group {Group} about their unlocked assignments", groupId);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Posts the group's unlock count in its Toeristen channel when it differs from the last one posted.</summary>
    /// <returns>True when a message was posted.</returns>
    public async Task<bool> NotifyAsync(Guid groupId, CancellationToken ct = default)
    {
        // Another instance may handle a check-in of the same group at the same moment: one at a time per group, and
        // with the last posted count as saved by the other instance, so a notice is not posted twice
        await using var groupLock = await _cluster.AcquireAsync(ClusterLockNames.Unlock(groupId), ct);
        if (_cluster.IsDistributed) _runtime.Reload();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var group = await services.GetRequiredService<IGroupRepository>().GetByIdAsync(groupId, ct);
        if (group == null || string.IsNullOrWhiteSpace(group.SlackChannelId)) return false;

        var checkIns = await services.GetRequiredService<ISubmissionRepository>()
            .SearchSubmissionsAsync(new SubmissionFilter { GroupId = groupId, Kind = SubmissionKind.CheckIn }, ct);
        var count = AdventurersScoring.ApprovedLocations(checkIns).Count;
        var last = _runtime.UnlockNotified(groupId) ?? 0;
        if (count == last) return false;

        var pool = _runtime.UnlockPool;
        var locations = await services.GetRequiredService<ILocationRepository>().GetAllAsync(ct);
        var text = Render(count > last ? _runtime.Text(SlackText.UnlockNotice) : _runtime.Text(SlackText.UnlockCorrection), count, last, pool, locations.Count);
        if (string.IsNullOrWhiteSpace(text))
        {
            _runtime.MarkUnlockNotified(groupId, count);
            return false;
        }

        var slack = services.GetRequiredService<ISlackClient>();
        var error = await slack.PostMessageAsync(group.SlackChannelId, text, ct);
        if (error != null && slack.IsConnected)
        {
            _logger.LogWarning("Could not post the unlock count {Count} of {Group} in {Channel}: {Error}", count, group.Name, group.SlackChannelId, error);
            return false;
        }

        _runtime.MarkUnlockNotified(groupId, count);
        _logger.LogInformation("Told the Toeristen of {Group} they have {Count} unlocked assignment(s) (was {Last})", group.Name, count, last);
        return error == null;
    }

    /// <summary>The notice with its placeholders filled in.</summary>
    /// <param name="locationCount">The number of locations, the total in count mode.</param>
    public static string Render(string? template, int count, int previous, IReadOnlyList<string> pool, int locationCount)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var total = pool.Count > 0 ? pool.Count.ToString() : locationCount > 0 ? locationCount.ToString() : null;
        // Without a pool or locations there is no total: "opdracht 4 van {total}" becomes "opdracht 4"
        if (total == null) template = template.Replace(" van {total}", "").Replace("{total}", "");
        var assignment = "";
        if (pool.Count > 0 && count > previous)
        {
            var codes = pool.Skip(previous).Take(count - previous).ToList();
            if (codes.Count > 0)
            {
                assignment = codes.Count == 1
                    ? $"Jullie kunnen nu opdracht {codes[0]} doen."
                    : $"Jullie kunnen nu opdracht {string.Join(", ", codes.Take(codes.Count - 1))} en {codes[^1]} doen.";
            }
        }

        return template
            .Replace("{count}", count.ToString())
            .Replace("{total}", total)
            .Replace("{assignment}", assignment)
            .Trim();
    }
}
