namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Lets a rebuild pause live Slack processing. Live events run side by side as usual;
/// while a rebuild holds the gate, new events wait until it is done.
/// </summary>
public class SlackProcessingGate
{
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
                    return new Releaser(Leave);
                }

                open = _open.Task;
            }

            await open.WaitAsync(ct);
        }
    }

    /// <summary>Stops new live events and waits for running ones. Disposing reopens the gate.</summary>
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

        await drained.WaitAsync(ct);
        return new Releaser(Reopen);
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
