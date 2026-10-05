using Microsoft.AspNetCore.Components;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Web.Components.Shared;

/// <summary>
/// Base for pages that show submission data and reload it when it changes.
/// Every load runs in a fresh DI scope: the circuit's own DbContext lives as long as the
/// browser tab and would keep returning stale tracked entities.
/// </summary>
public abstract class LiveComponentBase : ComponentBase, IDisposable
{
    // Coalesces bursts, e.g. one Slack message with several files
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private CancellationTokenSource? _pendingReload;
    private bool _disposed;

    [Inject] private ISubmissionChangeNotifier Notifier { get; set; } = default!;
    [Inject] private IServiceScopeFactory ScopeFactory { get; set; } = default!;
    [Inject] private ILogger<LiveComponentBase> Logger { get; set; } = default!;

    /// <summary>Loads the page's data using services from <paramref name="services"/>.</summary>
    protected abstract Task LoadAsync(IServiceProvider services);

    protected override void OnInitialized()
    {
        Notifier.Changed += OnSubmissionsChanged;
    }

    protected async Task ReloadAsync()
    {
        await _loadLock.WaitAsync();
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            await LoadAsync(scope.ServiceProvider);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Runs an action (e.g. a write) in a fresh DI scope.</summary>
    protected async Task RunInNewScopeAsync(Func<IServiceProvider, Task> action)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        await action(scope.ServiceProvider);
    }

    private void OnSubmissionsChanged()
    {
        if (_disposed) return;

        var reload = new CancellationTokenSource();
        Interlocked.Exchange(ref _pendingReload, reload)?.Cancel();
        _ = ReloadAfterDelayAsync(reload.Token);
    }

    private async Task ReloadAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(ReloadDelay, token);
            await InvokeAsync(async () =>
            {
                await ReloadAsync();
                StateHasChanged();
            });
        }
        catch (OperationCanceledException)
        {
            // A newer change replaced this reload
        }
        catch (Exception ex) when (_disposed || ex is ObjectDisposedException)
        {
            // Page closed while reloading
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Live reload of {Page} failed", GetType().Name);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Notifier.Changed -= OnSubmissionsChanged;
        _pendingReload?.Cancel();
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }
}
