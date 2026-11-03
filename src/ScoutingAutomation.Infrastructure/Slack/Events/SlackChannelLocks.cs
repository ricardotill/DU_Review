using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Slack.Events;

/// <summary>
/// One lock per Slack channel. Live events, the catch-up and a dashboard override all change the submissions of a
/// channel and move their files; holding the channel's lock makes them take turns, so one never moves a file the
/// other just moved. With several instances on one database the channel's cluster lock is held too, so they take turns
/// across instances.
/// </summary>
public sealed class SlackChannelLocks
{
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private readonly object _lock = new();
    private readonly IClusterLocks _cluster;

    public SlackChannelLocks(IClusterLocks? cluster = null)
    {
        _cluster = cluster ?? SingleInstance.Instance;
    }

    /// <summary>Waits for the channel's turn; disposing the result gives the turn back.</summary>
    public async Task<IDisposable> EnterAsync(string? channel, CancellationToken ct = default)
    {
        SemaphoreSlim semaphore;
        lock (_lock)
        {
            var key = channel ?? "";
            if (!_locks.TryGetValue(key, out semaphore!))
            {
                semaphore = new SemaphoreSlim(1, 1);
                _locks[key] = semaphore;
            }
        }

        await semaphore.WaitAsync(ct);
        try
        {
            var shared = await _cluster.AcquireAsync(ClusterLockNames.Channel(channel), ct);
            return new Releaser(semaphore, shared);
        }
        catch
        {
            semaphore.Release();
            throw;
        }
    }

    private sealed class Releaser(SemaphoreSlim semaphore, ClusterLock cluster) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            cluster.Dispose();
            semaphore.Release();
        }
    }
}
