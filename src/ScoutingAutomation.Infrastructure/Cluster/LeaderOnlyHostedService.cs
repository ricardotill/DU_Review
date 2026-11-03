using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>
/// Runs a hosted service only on the leader: started when this instance becomes the leader, stopped when it no longer
/// is, and started again when it becomes the leader again. With a single instance it simply runs.
/// </summary>
public sealed class LeaderOnlyHostedService<T> : IHostedService, IDisposable where T : IHostedService
{
    private readonly T _inner;
    private readonly IClusterLeadership _leadership;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _signal = new(0);
    private CancellationTokenSource? _stopping;
    private Task? _loop;
    private bool _running;

    public LeaderOnlyHostedService(T inner, IClusterLeadership leadership, ILogger<LeaderOnlyHostedService<T>> logger)
    {
        _inner = inner;
        _leadership = leadership;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // As before for a single instance: the service starts with the app
        if (_leadership.IsLeader)
        {
            LeaderWork.Enter(_leadership);
            await _inner.StartAsync(cancellationToken);
            _running = true;
        }

        _stopping = new CancellationTokenSource();
        _leadership.Changed += OnChanged;
        _loop = FollowLeadershipAsync(_stopping.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _leadership.Changed -= OnChanged;
        // The host may stop twice (a test stops it, then disposes it)
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping != null)
        {
            await stopping.CancelAsync();
            try
            {
                if (_loop != null) await _loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }

            stopping.Dispose();
        }

        if (_running)
        {
            _running = false;
            await _inner.StopAsync(cancellationToken);
        }
    }

    private void OnChanged() => _signal.Release();

    private async Task FollowLeadershipAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                if (_leadership.IsLeader && !_running)
                {
                    _logger.LogInformation("Starting {Service}: this instance is the leader", typeof(T).Name);
                    LeaderWork.Enter(_leadership);
                    await _inner.StartAsync(ct);
                    _running = true;
                }
                else if (!_leadership.IsLeader && _running)
                {
                    _logger.LogInformation("Stopping {Service}: another instance is the leader now", typeof(T).Name);
                    _running = false;
                    await _inner.StopAsync(CancellationToken.None);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not follow the leadership for {Service}", typeof(T).Name);
            }
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopping, null)?.Dispose();
    }
}
