using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Announcements;

/// <summary>
/// Reminds the jury in their own channel when a submission waits too long for a review.
/// Each message gets one reminder. The waiting time can be changed in the app while it runs.
/// </summary>
public class ReviewReminderService : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>The most messages listed in one reminder; the rest is counted.</summary>
    public const int MaxLines = 15;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RuntimeSettingsService _runtime;
    private readonly SlackLinks _links;
    private readonly TimeProvider _time;
    private readonly ILogger<ReviewReminderService> _logger;
    // Kept in memory: after a restart a message gets at most one more reminder
    private readonly HashSet<(string Channel, string Ts)> _reminded = new();

    public ReviewReminderService(
        IServiceScopeFactory scopeFactory,
        RuntimeSettingsService runtime,
        SlackLinks links,
        TimeProvider time,
        ILogger<ReviewReminderService> logger)
    {
        _scopeFactory = scopeFactory;
        _runtime = runtime;
        _links = links;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(CheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RemindAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Reminding the jury failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Posts one reminder for every open message that waits longer than the set time and had none yet.</summary>
    /// <returns>The number of messages in the reminder; 0 when nothing was posted.</returns>
    public async Task<int> RemindAsync(CancellationToken ct = default)
    {
        if (_runtime.ReviewReminderMinutes is not { } minutes || _runtime.ReminderChannelId is not { } channel || _runtime.ManualMode)
        {
            return 0;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var slack = scope.ServiceProvider.GetRequiredService<ISlackClient>();
        var queue = await ReviewQueue.LoadAsync(scope.ServiceProvider.GetRequiredService<ISubmissionRepository>(), ct);

        var now = _time.GetUtcNow();
        var threshold = TimeSpan.FromMinutes(minutes);
        var due = queue
            .Where(i => !i.AlreadyCompleted && i.WaitingFor(now) >= threshold && !_reminded.Contains((i.ChannelId, i.MessageTs)))
            .ToList();
        if (due.Count == 0) return 0;

        await _links.EnsureLoadedAsync(ct);
        var text = Render(due, minutes, now);
        var error = await slack.PostMessageAsync(channel, text, ct);
        if (error is not (null or "simulation_mode"))
        {
            // Not marked: the next check tries again
            _logger.LogWarning("Could not post the review reminder in {Channel}: {Error}", channel, error);
            return 0;
        }

        foreach (var item in due)
        {
            _reminded.Add((item.ChannelId, item.MessageTs));
        }

        _logger.LogInformation("Reminded the jury of {Count} submission(s) waiting longer than {Minutes} min", due.Count, minutes);
        return due.Count;
    }

    private string Render(IReadOnlyList<ReviewQueueItem> due, int minutes, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.Append($":hourglass: *{due.Count} {(due.Count == 1 ? "inzending wacht" : "inzendingen wachten")} langer dan {minutes} min op een beoordeling:*");
        foreach (var item in due.Take(MaxLines))
        {
            var group = item.Group?.Abbreviation is { Length: > 0 } abbreviation ? abbreviation : item.Group?.Name ?? "Onbekende groep";
            var label = $"{group} – opdracht {item.AssignmentCode ?? "?"}";
            var link = _links.WebLink(item.ChannelId, item.MessageTs);
            text.Append($"\n• {(link == null ? label : $"<{link}|{label}>")} ({ReviewQueue.FormatWait(item.WaitingFor(now))})");
        }

        if (due.Count > MaxLines)
        {
            text.Append($"\n…en {due.Count - MaxLines} meer");
        }

        return text.ToString();
    }
}
