using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>
/// Tells a group which assignments their Avonturiers opened: in the Toeristen channel, and on the group's open pages.
/// The workflow only queues the group while it holds the Avonturiers channel's lock, and the organisation queues it
/// when it opens locations; the count is read and posted here afterwards, so two channel locks are never held at once.
/// Each Toeristen channel hears a number once: a change back and forth before the post is quiet.
/// Catch-up and rebuild do not queue anything, so Slack never gets an old notice twice.
/// A check-in only counts from the end of its round (see <see cref="AssignmentUnlocks.ReleaseAt"/>), so every group is
/// also queued at each round end that passes while the app runs; a round end from before the start is not announced.
/// </summary>
public sealed class UnlockNotifier : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettingsService _runtime;
    private readonly ILogger<UnlockNotifier> _logger;
    private readonly TimeProvider _time;
    private readonly object _wakeLock = new();
    private CancellationTokenSource _wake = new();
    private int _pending;

    // What the group's open pages were last told, apart from Slack: a Slack post that is tried again must not
    // announce the same assignments on the pages twice. Not saved: after a restart no page is open yet.
    private readonly Dictionary<Guid, int> _pagesTold = new();

    public UnlockNotifier(IServiceScopeFactory scopeFactory, RuntimeSettingsService runtime, ILogger<UnlockNotifier> logger,
        TimeProvider? time = null)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Groups queued and not handled yet; for tests and the events page.</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Queues a check of the group's open assignments after a live change to its check-ins or open locations.</summary>
    public void Enqueue(Guid groupId)
    {
        if (!_runtime.AdventurersEnabled) return;

        Interlocked.Increment(ref _pending);
        if (!_queue.Writer.TryWrite(groupId)) Interlocked.Decrement(ref _pending);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ReadQueueAsync(stoppingToken), WatchRoundEndsAsync(stoppingToken));

    private async Task ReadQueueAsync(CancellationToken stoppingToken)
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

    /// <summary>Queues every group at each round end, when the check-ins of that round start to count.</summary>
    private async Task WatchRoundEndsAsync(CancellationToken stoppingToken)
    {
        var handledUntil = _time.GetUtcNow();
        _runtime.Changed += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken wake;
                lock (_wakeLock) wake = _wake.Token;

                try
                {
                    handledUntil = await QueueRoundEndsAsync(handledUntil, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Could not queue the groups at the end of a round; trying again in a minute");
                    await WaitAsync(_time.GetUtcNow() + TimeSpan.FromMinutes(1), wake, stoppingToken);
                    continue;
                }

                await WaitAsync(NextRoundEnd(handledUntil), wake, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _runtime.Changed -= Wake;
        }
    }

    /// <summary>Queues every group when a round ended after <paramref name="handledUntil"/>; returns the new mark.</summary>
    public async Task<DateTimeOffset> QueueRoundEndsAsync(DateTimeOffset handledUntil, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if (!_runtime.AdventurersEnabled || !_runtime.ThemeRounds.Any(r => r.End > handledUntil && r.End <= now)) return now;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var groups = await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
        _logger.LogInformation("A round ended: checking the open assignments of {Count} group(s)", groups.Count);
        foreach (var group in groups) Enqueue(group.Id);
        // The overviews count the open assignments too
        scope.ServiceProvider.GetRequiredService<ISubmissionChangeNotifier>().NotifyChanged();
        return now;
    }

    private DateTimeOffset? NextRoundEnd(DateTimeOffset after) =>
        _runtime.ThemeRounds.Where(r => r.End > after).Select(r => (DateTimeOffset?)r.End).Min();

    private async Task WaitAsync(DateTimeOffset? until, CancellationToken wake, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wake, stoppingToken);
        try
        {
            if (until == null)
            {
                await Task.Delay(Timeout.Infinite, linked.Token);
                return;
            }

            // A day at most at a time: Task.Delay refuses waits over 49 days
            var remaining = until.Value - _time.GetUtcNow();
            while (remaining > TimeSpan.Zero)
            {
                var step = remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining;
                await Task.Delay(step, _time, linked.Token);
                remaining -= step;
            }
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
        }
    }

    // A new source, so the next wait is not cancelled at once; the old one is not disposed, the loop may still link to it
    private void Wake()
    {
        CancellationTokenSource old;
        lock (_wakeLock)
        {
            old = _wake;
            _wake = new CancellationTokenSource();
        }

        old.Cancel();
    }

    public override void Dispose()
    {
        lock (_wakeLock) _wake.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Posts the group's number of open assignments in its Toeristen channel when it differs from the last one posted,
    /// and tells the group's open pages about new ones.
    /// </summary>
    /// <returns>True when a message was posted.</returns>
    public async Task<bool> NotifyAsync(Guid groupId, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var group = await services.GetRequiredService<IGroupRepository>().GetByIdAsync(groupId, ct);
        if (group == null) return false;

        var state = await services.GetRequiredService<AssignmentUnlockService>().ForGroupAsync(groupId, ct);
        if (state == null) return false;

        // Never told: the group knows the assignments that are open from the start
        var last = _runtime.UnlockNotified(groupId) ?? Math.Min(_runtime.InitialOpenAssignments, state.Total);
        var count = state.OpenCount;
        if (count == last) return false;

        // Without locations everything is open: that is no news. Nothing is remembered either, so the count that
        // follows once the locations are in is not taken for a drop from "everything"
        if ((await services.GetRequiredService<ILocationRepository>().GetAllAsync(ct)).Count == 0) return false;

        var codes = (await services.GetRequiredService<IAssignmentRepository>().GetAllAsync(ct)).Select(a => a.Code).ToList();
        var opened = AssignmentUnlocks.NewlyOpened(last, count, codes);
        // The pages first: they do not depend on Slack. Only what they were not told yet
        var forPages = PagesNotYetTold(groupId, last, count, codes);
        if (forPages.Count > 0)
        {
            services.GetRequiredService<ISubmissionChangeNotifier>().NotifyUnlocked(new AssignmentsUnlockedNotification(groupId, forPages, count, state.Total));
        }

        var text = Render(count > last ? _runtime.Text(SlackText.UnlockNotice) : _runtime.Text(SlackText.UnlockCorrection), count, state.Total, opened);
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(group.SlackChannelId))
        {
            _runtime.MarkUnlockNotified(groupId, count);
            return false;
        }

        var slack = services.GetRequiredService<ISlackClient>();
        var error = await slack.PostMessageAsync(group.SlackChannelId, text, ct);
        if (error != null && slack.IsConnected)
        {
            _logger.LogWarning("Could not post the {Count} open assignment(s) of {Group} in {Channel}: {Error}", count, group.Name, group.SlackChannelId, error);
            return false;
        }

        _runtime.MarkUnlockNotified(groupId, count);
        _logger.LogInformation("Told the Toeristen of {Group} they have {Count} open assignment(s) (was {Last})", group.Name, count, last);
        return error == null;
    }

    /// <summary>The newly opened assignments the pages were not told yet; remembers the count told now.</summary>
    private IReadOnlyList<string> PagesNotYetTold(Guid groupId, int last, int count, IReadOnlyList<string> codes)
    {
        lock (_pagesTold)
        {
            var told = Math.Max(last, _pagesTold.GetValueOrDefault(groupId, last));
            _pagesTold[groupId] = count;
            return AssignmentUnlocks.NewlyOpened(told, count, codes);
        }
    }

    /// <summary>The notice with its placeholders filled in.</summary>
    /// <param name="count">Numbered assignments open now.</param>
    /// <param name="total">All numbered assignments.</param>
    /// <param name="opened">The assignments that just opened; empty for a correction.</param>
    public static string Render(string? template, int count, int total, IReadOnlyList<string> opened)
    {
        if (string.IsNullOrWhiteSpace(template)) return "";

        var assignment = opened.Count switch
        {
            0 => "",
            1 => $"Jullie kunnen nu opdracht {opened[0]} doen.",
            _ => $"Jullie kunnen nu opdracht {string.Join(", ", opened.Take(opened.Count - 1))} en {opened[^1]} doen."
        };

        return template
            .Replace("{count}", count.ToString())
            .Replace("{total}", total.ToString())
            .Replace("{assignment}", assignment)
            .Trim();
    }
}
