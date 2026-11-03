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
/// message when it ends, and the result once the jury reviewed every submission of the round. When a round starts as
/// the previous one ends, one handover message replaces the closing and the opening. Each channel gets each
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

    /// <summary>The handover text: <paramref name="previous"/> ends as <paramref name="round"/> starts.</summary>
    public static string RenderHandover(string template, ThemeRound previous, ThemeRound round) =>
        Render(template
            .Replace("{previousRound}", previous.Number.ToString())
            .Replace("{previousThemes}", previous.ThemesLabel), round);

    /// <summary>
    /// Posts one message of a round in every Avonturiers channel that did not get it yet, or in all of them with
    /// <paramref name="resend"/>. A result is posted even while the round is provisional when an admin asks for it.
    /// </summary>
    public async Task<IReadOnlyList<AnnouncementPostResult>> PostAsync(int roundNumber, RoundMessage message, bool resend,
        CancellationToken ct = default, MessagePicture? picture = null)
    {
        var round = _runtime.ThemeRounds.FirstOrDefault(r => r.Number == roundNumber);
        if (round == null || string.IsNullOrWhiteSpace(Template(message)))
        {
            _logger.LogWarning("No round {Round} or no {Message} text is set; nothing posted", roundNumber, message);
            return Array.Empty<AnnouncementPostResult>();
        }

        return await PostPartsAsync(new[] { (round, message) }, combined: null, resend, picture, ct);
    }

    /// <summary>
    /// Posts the given messages as one Slack message per Avonturiers channel: each channel gets only the parts it did
    /// not get yet (all of them with <paramref name="resend"/>), and every part it got is saved as posted.
    /// A channel that needs every part gets <paramref name="combined"/> when set; otherwise the texts of its parts,
    /// leaving out the parts without a text.
    /// </summary>
    private async Task<IReadOnlyList<AnnouncementPostResult>> PostPartsAsync(
        IReadOnlyList<(ThemeRound Round, RoundMessage Message)> parts, string? combined, bool resend, MessagePicture? picture,
        CancellationToken ct)
    {
        await _posting.WaitAsync(ct);
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var groups = await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
            var slack = scope.ServiceProvider.GetRequiredService<ISlackClient>();

            AdventurersOverview? overview = null;
            var texts = new List<(ThemeRound Round, RoundMessage Message, string? Text)>();
            foreach (var (round, message) in parts)
            {
                var template = Template(message);
                if (string.IsNullOrWhiteSpace(template))
                {
                    texts.Add((round, message, null));
                    continue;
                }

                var ranking = "";
                if (message == RoundMessage.Result)
                {
                    overview ??= await scope.ServiceProvider.GetRequiredService<AdventurersScoreService>().GetOverviewAsync(ct);
                    var result = overview.Standing.Rounds.FirstOrDefault(r => r.Round.Number == round.Number);
                    var names = groups.ToDictionary(g => g.Id, g => g.Name);
                    ranking = result?.Ranking(id => names.GetValueOrDefault(id, "?")) ?? "";
                }

                texts.Add((round, message, Render(template, round, ranking)));
            }

            // Only the open message carries the round's picture; a picture on a later message is the admin's choice
            var results = new List<AnnouncementPostResult>();
            foreach (var group in groups.Where(g => !string.IsNullOrWhiteSpace(g.AdventurersSlackChannelId)))
            {
                var channel = group.AdventurersSlackChannelId!;
                var due = texts.Where(t => resend || PostedAt(t.Round.Number, t.Message, channel) == null).ToList();
                if (due.Count == 0)
                {
                    results.Add(new AnnouncementPostResult(group.Name, channel, Posted: false, Skipped: true, Error: null));
                    continue;
                }

                if (combined == null || due.Count < texts.Count) due = due.Where(t => t.Text != null).ToList();
                if (due.Count == 0) continue;

                var text = combined != null && due.Count == texts.Count
                    ? combined
                    : string.Join("\n\n", due.Select(t => t.Text));
                var what = string.Join(" and ", due.Select(t => $"{t.Message} of round {t.Round.Number}"));
                var error = picture == null
                    ? await slack.PostMessageAsync(channel, text, ct)
                    : await slack.PostFileAsync(channel, picture.Content, GroupMessageSender.SafeFileName(picture.FileName), text, ct);
                if (error == null)
                {
                    _logger.LogInformation("Posted the {Messages} message for {Group}", what, group.Name);
                    try
                    {
                        foreach (var t in due) _runtime.MarkAdventurersPosted(Key(t.Round.Number, t.Message), channel, _time.GetUtcNow());
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogError(ex, "Posted the {Messages} message in {Channel}, but could not save that", what, channel);
                    }
                }
                else
                {
                    _logger.LogWarning("Could not post the {Messages} message for {Group}: {Error}", what, group.Name, error);
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
                // A round that starts as this one ends: one handover message instead, unless its text is empty
                var following = rounds.FirstOrDefault(r => r.Start == round.End);
                var handover = _runtime.Text(SlackText.RoundHandover);
                if (following != null && !string.IsNullOrWhiteSpace(handover) && following.End > now
                    && now - following.Start <= ScheduledAnnouncer.AutoPostWindow && !Done(following, RoundMessage.Open))
                {
                    await PostPartsAsync(new[] { (round, RoundMessage.Close), (following, RoundMessage.Open) },
                        RenderHandover(handover, round, following), resend: false, picture: null, ct);
                }
                else
                {
                    await PostAsync(round.Number, RoundMessage.Close, resend: false, ct);
                }
            }

            // A result waits for the jury, but only for a round this app closed itself: a late start posts no old results
            var closedHere = now - round.End <= ScheduledAnnouncer.AutoPostWindow || Done(round, RoundMessage.Close);
            if (closedHere && now - round.End <= ResultWindow && !Done(round, RoundMessage.Result))
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
