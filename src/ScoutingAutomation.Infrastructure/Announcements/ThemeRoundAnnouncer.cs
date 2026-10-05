using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Announcements;

/// <summary>The three messages of a theme round.</summary>
public enum RoundMessage
{
    Open,
    Close,
    Result
}

/// <summary>
/// Posts the messages of the theme rounds in every Avonturiers channel: the round text when a round opens, a closing
/// message when it ends, and the result once the jury reviewed every submission of the round. Each channel gets each
/// message once; that is saved, so a restart does not post again. Admins can post or repost by hand.
/// </summary>
public sealed class ThemeRoundAnnouncer : BackgroundService
{
    /// <summary>How long after a round's end its result is still posted by itself, for a jury that takes long.</summary>
    public static readonly TimeSpan ResultWindow = TimeSpan.FromHours(4);

    /// <summary>How often a closed round without a result is checked again, besides on every review.</summary>
    public static readonly TimeSpan ResultCheckInterval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly RuntimeSettingsService _runtime;
    private readonly ISubmissionChangeNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<ThemeRoundAnnouncer> _logger;
    private readonly SemaphoreSlim _posting = new(1, 1);
    private readonly object _wakeLock = new();
    private CancellationTokenSource _wake = new();

    public ThemeRoundAnnouncer(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        RuntimeSettingsService runtime,
        ISubmissionChangeNotifier notifier,
        TimeProvider time,
        ILogger<ThemeRoundAnnouncer> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _runtime = runtime;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    /// <summary>The key a message of a round is saved under in the runtime settings, e.g. "round-3-open".</summary>
    public static string Key(int round, RoundMessage message) => $"round-{round}-{message.ToString().ToLowerInvariant()}";

    /// <summary>When the channel got that message of the round; null when it did not.</summary>
    public DateTimeOffset? PostedAt(int round, RoundMessage message, string channelId) =>
        _runtime.AdventurersPostedAt(Key(round, message), channelId);

    /// <summary>The template of a message, as currently set.</summary>
    public string? Template(RoundMessage message) => _runtime.Text(message switch
    {
        RoundMessage.Open => SlackText.RoundOpen,
        RoundMessage.Close => SlackText.RoundClose,
        _ => SlackText.RoundResult
    });

    /// <summary>The message text, with every placeholder filled in.</summary>
    public static string Render(string template, ThemeRound round, string ranking = "") =>
        template
            .Replace("{round}", round.Number.ToString())
            .Replace("{themes}", round.ThemesLabel)
            .Replace("{start}", round.Start.ToGameTime().ToString("HH:mm"))
            .Replace("{end}", round.End.ToGameTime().ToString("HH:mm"))
            .Replace("{message}", round.Message ?? "")
            .Replace("{ranking}", ranking)
            .Trim();

    /// <summary>
    /// Posts one message of a round in every Avonturiers channel that did not get it yet, or in all of them with
    /// <paramref name="resend"/>. A result is posted even while the round is provisional when an admin asks for it.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementPostResult>> PostAsync(int roundNumber, RoundMessage message, bool resend,
        CancellationToken ct = default, MessagePicture? picture = null)
    {
        var round = _runtime.ThemeRounds.FirstOrDefault(r => r.Number == roundNumber);
        var template = Template(message);
        if (round == null || string.IsNullOrWhiteSpace(template))
        {
            _logger.LogWarning("No round {Round} or no {Message} text is set; nothing posted", roundNumber, message);
            return Array.Empty<AnnouncementPostResult>();
        }

        await _posting.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var groups = await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
            var slack = scope.ServiceProvider.GetRequiredService<ISlackClient>();

            var ranking = "";
            if (message == RoundMessage.Result)
            {
                var overview = await scope.ServiceProvider.GetRequiredService<AdventurersScoreService>().GetOverviewAsync(ct);
                var result = overview.Standing.Rounds.FirstOrDefault(r => r.Round.Number == roundNumber);
                var names = groups.ToDictionary(g => g.Id, g => g.Name);
                ranking = result?.Ranking(id => names.GetValueOrDefault(id, "?")) ?? "";
            }

            var text = Render(template, round, ranking);
            // Only the open message carries the round's picture; a picture on a later message is the admin's choice
            var results = new List<AnnouncementPostResult>();
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g.AdventurersSlackChannelId)))
            {
                var channel = group.AdventurersSlackChannelId!;
                if (!resend && PostedAt(roundNumber, message, channel) != null)
                {
                    results.Add(new AnnouncementPostResult(group.Name, channel, Posted: false, Skipped: true, Error: null));
                    continue;
                }

                var error = picture == null
                    ? await slack.PostMessageAsync(channel, text, ct)
                    : await slack.PostFileAsync(channel, picture.Content, GroupMessageSender.SafeFileName(picture.FileName), text, ct);
                if (error == null)
                {
                    _logger.LogInformation("Posted the {Message} message of round {Round} for {Group}", message, roundNumber, group.Name);
                    try
                    {
                        _runtime.MarkAdventurersPosted(Key(roundNumber, message), channel, _time.GetUtcNow());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError(ex, "Posted the {Message} message of round {Round} in {Channel}, but could not save that", message, roundNumber, channel);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not post the {Message} message of round {Round} for {Group}: {Error}", message, roundNumber, group.Name, error);
                }

                results.Add(new AnnouncementPostResult(group.Name, channel, Posted: error == null, Skipped: false, error));
            }

            return results;
        }
        finally
        {
            _posting.Release();
        }
    }

    /// <summary>
    /// Posts every message that is due now and was not posted yet; returns when to look again (null: only on a change).
    /// A message whose time passed more than <see cref="ScheduledAnnouncer.AutoPostWindow"/> ago is left to the admins,
    /// so a late start of the app does not post old rounds.
    /// </summary>
    public async Task<DateTimeOffset?> PostDueAsync(CancellationToken ct = default)
    {
        if (!_runtime.AdventurersEnabled) return null;

        var rounds = _runtime.ThemeRounds;
        var now = _time.GetUtcNow();
        DateTimeOffset? next = null;
        void Next(DateTimeOffset at) => next = next == null || at < next ? at : next;

        List<string> channels;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            channels = (await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct))
                .Select(g => g.AdventurersSlackChannelId)
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c!)
                .ToList();
        }
        if (channels.Count == 0) return null;

        bool Done(ThemeRound round, RoundMessage message) => channels.All(c => PostedAt(round.Number, message, c) != null);

        AdventurersOverview? overview = null;
        foreach (var round in rounds.OrderBy(r => r.Start))
        {
            if (round.Start > now) Next(round.Start);
            else if (now - round.Start <= ScheduledAnnouncer.AutoPostWindow && round.End > now && !Done(round, RoundMessage.Open))
            {
                await PostAsync(round.Number, RoundMessage.Open, resend: false, ct);
            }

            if (round.End > now)
            {
                Next(round.End);
                continue;
            }

            if (now - round.End <= ScheduledAnnouncer.AutoPostWindow && !Done(round, RoundMessage.Close))
            {
                await PostAsync(round.Number, RoundMessage.Close, resend: false, ct);
            }

            if (now - round.End <= ResultWindow && !Done(round, RoundMessage.Result))
            {
                if (overview == null)
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    overview = await scope.ServiceProvider.GetRequiredService<AdventurersScoreService>().GetOverviewAsync(ct);
                }

                var result = overview.Standing.Rounds.FirstOrDefault(r => r.Round.Number == round.Number);
                if (result is { IsProvisional: false })
                {
                    await PostAsync(round.Number, RoundMessage.Result, resend: false, ct);
                }
                else
                {
                    // Reviews wake the loop too; this catches a review that came in while posting
                    Next(now + ResultCheckInterval);
                }
            }
        }

        return next;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = _options.Value;
        if (settings.Slack.SimulationMode || string.IsNullOrWhiteSpace(settings.Slack.BotToken))
        {
            _logger.LogInformation("Not connected to Slack: the theme round messages are not posted by themselves");
            return;
        }

        _runtime.Changed += Wake;
        _notifier.Changed += Wake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                CancellationToken wake;
                lock (_wakeLock) wake = _wake.Token;

                DateTimeOffset? next;
                try
                {
                    next = await PostDueAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // A failing announcer must never stop the app: it tries again a bit later
                    _logger.LogError(ex, "The theme round announcer failed; trying again in a minute");
                    next = _time.GetUtcNow() + TimeSpan.FromMinutes(1);
                }

                await WaitAsync(next, wake, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _runtime.Changed -= Wake;
            _notifier.Changed -= Wake;
        }
    }

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
        _posting.Dispose();
        lock (_wakeLock) _wake.Dispose();
        base.Dispose();
    }
}
