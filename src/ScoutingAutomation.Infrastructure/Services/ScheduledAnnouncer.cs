using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>Outcome of posting an announcement in one group's channel.</summary>
/// <param name="Error">Slack's error code, or null when posted or skipped.</param>
public sealed record AnnouncementPostResult(string GroupName, string ChannelId, bool Posted, bool Skipped, string? Error);

/// <summary>
/// Posts a message in every group channel: by itself at a set time, or by hand from the app.
/// Each channel gets it once; the channels that got it are saved, so a restart does not post again.
/// The time can change in the app while it runs; the wait then starts over.
/// </summary>
public abstract class ScheduledAnnouncer : BackgroundService
{
    /// <summary>
    /// A start of the app more than this long after the set time does not post by itself: a late message
    /// would only confuse. An admin can still post by hand.
    /// </summary>
    public static readonly TimeSpan AutoPostWindow = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _posting = new(1, 1);
    private readonly object _wakeLock = new();
    private CancellationTokenSource _wake = new();

    protected ScheduledAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        TimeProvider time,
        ILogger logger)
    {
        _scopeFactory = scopeFactory;
        Options = options;
        Runtime = runtime;
        Time = time;
        Logger = logger;
    }

    protected IOptions<ScoutingSettings> Options { get; }
    protected RuntimeSettingsService Runtime { get; }
    protected TimeProvider Time { get; }
    protected ILogger Logger { get; }

    /// <summary>What the message is about, for the log, e.g. "game-start".</summary>
    protected abstract string Name { get; }

    /// <summary>When the message is posted by itself; null when there is no such time.</summary>
    protected abstract DateTimeOffset? PostTime { get; }

    /// <summary>Whether the message is posted by itself at <see cref="PostTime"/>.</summary>
    protected abstract bool AutoPost { get; }

    // Read on each use: an admin can change the text while the app runs
    protected abstract string? Template { get; }

    /// <summary>The message for one group from the template.</summary>
    protected abstract string Render(string template, string groupName);

    public abstract DateTimeOffset? PostedAt(string channelId);

    protected abstract void MarkPosted(string channelId, DateTimeOffset postedAt);

    /// <summary>The message as a group would get it; null when no message is set.</summary>
    public string? Preview(string groupName, string? template = null)
    {
        template ??= Template;
        return string.IsNullOrWhiteSpace(template) ? null : Render(template, groupName);
    }

    /// <summary>
    /// Posts the message in every group channel that did not get it yet, or in all channels with <paramref name="resend"/>.
    /// Runs one at a time, so the automatic post and an admin's click never post twice.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementPostResult>> PostAsync(bool resend, CancellationToken ct = default)
    {
        var template = Template;
        if (string.IsNullOrWhiteSpace(template))
        {
            Logger.LogWarning("No {Name} message is set; nothing posted", Name);
            return Array.Empty<AnnouncementPostResult>();
        }

        await _posting.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var groups = await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
            var slack = scope.ServiceProvider.GetRequiredService<ISlackClient>();

            var results = new List<AnnouncementPostResult>();
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g.SlackChannelId)))
            {
                if (!resend && PostedAt(group.SlackChannelId) != null)
                {
                    results.Add(new AnnouncementPostResult(group.Name, group.SlackChannelId, Posted: false, Skipped: true, Error: null));
                    continue;
                }

                var error = await slack.PostMessageAsync(group.SlackChannelId, Render(template, group.Name), ct);
                if (error == null)
                {
                    MarkPosted(group.SlackChannelId, Time.GetUtcNow());
                    Logger.LogInformation("Posted the {Name} message for {Group} in {Channel}", Name, group.Name, group.SlackChannelId);
                }
                else
                {
                    Logger.LogWarning("Could not post the {Name} message for {Group} in {Channel}: {Error}", Name, group.Name, group.SlackChannelId, error);
                }

                results.Add(new AnnouncementPostResult(group.Name, group.SlackChannelId, Posted: error == null, Skipped: false, error));
            }

            return results;
        }
        finally
        {
            _posting.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = Options.Value;
        if (settings.Slack.SimulationMode || string.IsNullOrWhiteSpace(settings.Slack.BotToken))
        {
            Logger.LogInformation("Not connected to Slack: the {Name} message is not posted by itself", Name);
            return;
        }

        Runtime.Changed += OnSettingsChanged;
        try
        {
            // Posted once per run of the app; a time changed afterwards does not post again by itself
            DateTimeOffset? postedFor = null;
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken wake;
                lock (_wakeLock) wake = _wake.Token;

                var at = PostTime;
                // The text is checked when posting: an admin can still set it before the time
                if (AutoPost && at is { } time && time != postedFor)
                {
                    var now = Time.GetUtcNow();
                    if (now - time > AutoPostWindow)
                    {
                        Logger.LogWarning("The {Name} time {Time} passed more than {Window} ago: the message is not posted by itself", Name, time, AutoPostWindow);
                    }
                    else if (await WaitUntilAsync(time, now, wake, stoppingToken))
                    {
                        postedFor = time;
                        await TryPostAsync(stoppingToken);
                        continue;
                    }
                    else
                    {
                        // The settings changed: look at the time again
                        continue;
                    }
                }

                // Nothing to do until the settings change
                await WaitForChangeAsync(wake, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Runtime.Changed -= OnSettingsChanged;
        }
    }

    /// <returns>True when the time came; false when the settings changed first.</returns>
    private async Task<bool> WaitUntilAsync(DateTimeOffset time, DateTimeOffset now, CancellationToken wake, CancellationToken stoppingToken)
    {
        if (time <= now) return true;

        Logger.LogInformation("The {Name} message is posted at {Time}", Name, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wake, stoppingToken);
        try
        {
            await Task.Delay(time - now, Time, linked.Token);
            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static async Task WaitForChangeAsync(CancellationToken wake, CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(wake, stoppingToken);
        try
        {
            await Task.Delay(Timeout.Infinite, linked.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TryPostAsync(CancellationToken stoppingToken)
    {
        try
        {
            await PostAsync(resend: false, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            Logger.LogError(ex, "Posting the {Name} message failed", Name);
        }
    }

    // Wakes the loop so it reads the time again; a new source, so the next wait is not cancelled at once.
    // The old source is not disposed: the loop may still be linking to its token
    private void OnSettingsChanged()
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
        _posting.Dispose();
        lock (_wakeLock) _wake.Dispose();
        base.Dispose();
    }
}
