using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Slack.Events;

/// <summary>
/// Lets a rebuild pause live Slack processing. Live events run side by side as usual;
/// while a rebuild holds the gate, new events wait until it is done.
/// With several instances on one database the gate also holds the cluster lock <see cref="ClusterLockNames.SlackGate"/>:
/// shared for a live event, alone for a rebuild, so a rebuild on one instance pauses the others too.
/// </summary>
public class SlackProcessingGate
{
    private readonly IClusterLocks _cluster;
    private readonly ActiveEditionRefresher? _editions;

    /// <param name="cluster">The locks shared with other instances; none when left out.</param>
    /// <param name="editions">
    /// Reads the active edition after entering, when other instances can switch it: an edition started elsewhere while this
    /// event waited must not get its files in the old edition's folder.
    /// </param>
    public SlackProcessingGate(IClusterLocks? cluster = null, ActiveEditionRefresher? editions = null)
    {
        _cluster = cluster ?? SingleInstance.Instance;
        _editions = editions;
    }

    private readonly object _lock = new();
    private TaskCompletionSource _open = CreateOpen();
    private TaskCompletionSource? _drained;
    private int _active;

    public bool IsClosed
    {
        get { lock (_lock) return !_open.Task.IsCompleted; }
    }

    /// <summary>Waits while a rebuild runs, then marks one live event as in progress until disposed.</summary>
    public async Task<IDisposable> EnterAsync(CancellationToken ct = default)
    {
        while (true)
        {
            Task open;
            lock (_lock)
            {
                if (_open.Task.IsCompleted)
                {
                    _active++;
                    open = Task.CompletedTask;
                }
                else
                {
                    open = _open.Task;
                }
            }

            if (!open.IsCompleted)
            {
                await open.WaitAsync(ct);
                continue;
            }

            if (!_cluster.IsDistributed) return new Releaser(Leave);

            ClusterLock? shared = null;
            try
            {
                shared = await _cluster.AcquireSharedAsync(ClusterLockNames.SlackGate, ct);
                if (_editions != null) await _editions.RefreshAsync(ct);
                var held = shared;
                return new Releaser(() =>
                {
                    held.Dispose();
                    Leave();
                });
            }
            catch
            {
                shared?.Dispose();
                Leave();
                throw;
            }
        }
    }

    /// <summary>
    /// Stops new live events and waits for running ones. Disposing reopens the gate.
    /// When the wait is cancelled, the gate opens again right away, so live processing never stays paused.
    /// </summary>
    public async Task<IDisposable> CloseAsync(CancellationToken ct = default)
    {
        Task drained;
        lock (_lock)
        {
            if (!_open.Task.IsCompleted)
            {
                throw new InvalidOperationException("Live processing is already paused");
            }

            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_active == 0) _drained.SetResult();
            drained = _drained.Task;
        }

        ClusterLock? alone = null;
        try
        {
            await drained.WaitAsync(ct);
            // Waits for the live events of the other instances too
            alone = await _cluster.AcquireAsync(ClusterLockNames.SlackGate, ct);
        }
        catch
        {
            Reopen();
            throw;
        }

        return new Releaser(() =>
        {
            alone.Dispose();
            Reopen();
        });
    }

    private void Leave()
    {
        lock (_lock)
        {
            _active--;
            if (_active == 0) _drained?.TrySetResult();
        }
    }

    private void Reopen()
    {
        lock (_lock) _open.TrySetResult();
    }

    private static TaskCompletionSource CreateOpen()
    {
        var open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        open.SetResult();
        return open;
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) release();
        }
    }
}
