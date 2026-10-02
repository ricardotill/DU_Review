using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Interfaces;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>Outcome of one catch-up run.</summary>
public sealed record CatchUpResult(DateTimeOffset FinishedAt, int MessagesChecked, int SubmissionsAdded, int ReviewsUpdated, IReadOnlyList<string> ChannelErrors);

public interface ISlackCatchUpService
{
    /// <summary>Asks for a catch-up. Requests while one runs lead to one more run afterwards.</summary>
    void Request(string reason);

    /// <summary>The latest finished run, or null when none ran yet.</summary>
    CatchUpResult? LastResult { get; }

    /// <summary>The running or last run, for waiting on it.</summary>
    Task Completion { get; }
}

/// <summary>
/// Picks up what Slack sent while the app was not listening: after a restart, a crash or a lost connection.
/// Reads today's messages of every group channel and processes what is missing, without deleting anything.
/// Socket Mode asks for a run on every (re)connect; with the Events API it runs once at startup.
/// </summary>
public class SlackCatchUpService : BackgroundService, ISlackCatchUpService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly SlackProcessingGate _gate;
    private readonly RuntimeSettingsService _runtime;
    private readonly ILogger<SlackCatchUpService> _logger;
    private readonly object _lock = new();
    private CancellationToken _stopping = CancellationToken.None;
    private bool _running;
    private bool _pending;

    public SlackCatchUpService(
        IServiceScopeFactory scopeFactory,
        IOptions<ScoutingSettings> options,
        SlackProcessingGate gate,
        RuntimeSettingsService runtime,
        ILogger<SlackCatchUpService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _gate = gate;
        _runtime = runtime;
        _logger = logger;
    }

    public CatchUpResult? LastResult { get; private set; }

    public Task Completion { get; private set; } = Task.CompletedTask;

    private bool IsAvailable =>
        !_options.Value.Slack.SimulationMode && !string.IsNullOrWhiteSpace(_options.Value.Slack.BotToken);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // Socket Mode asks itself once connected; the Events API has no connection to wait for
        if (!_options.Value.Slack.SocketMode)
        {
            Request("startup");
        }

        return Task.CompletedTask;
    }

    public void Request(string reason)
    {
        if (!IsAvailable) return;

        lock (_lock)
        {
            if (_running)
            {
                _pending = true;
                return;
            }

            _running = true;
            _logger.LogInformation("Catching up on Slack messages ({Reason})", reason);
            Completion = Task.Run(RunUntilDoneAsync);
        }
    }

    private async Task RunUntilDoneAsync()
    {
        while (true)
        {
            try
            {
                await RunOnceAsync(_stopping);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Catching up on Slack messages failed");
            }

            lock (_lock)
            {
                if (!_pending)
                {
                    _running = false;
                    return;
                }

                _pending = false;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        if (_runtime.ManualMode)
        {
            // Manual mode does not remember which files it saved, so a catch-up would save them all again
            _logger.LogWarning("Manual mode is on: skipped catching up on Slack messages. Rebuild from Slack if messages were missed");
            return;
        }

        // Waits while a rebuild runs; a rebuild started now waits until this is done
        using var _ = await _gate.EnterAsync(ct);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var groups = await scope.ServiceProvider.GetRequiredService<IGroupRepository>().GetAllAsync(ct);
        var history = scope.ServiceProvider.GetRequiredService<ISlackHistoryClient>();
        var workflow = scope.ServiceProvider.GetRequiredService<ISubmissionWorkflowService>();
        var links = scope.ServiceProvider.GetService<SlackLinks>();
        if (links != null) await links.EnsureLoadedAsync(ct);
        var botUserId = links?.BotUserId;

        // The game takes one day: everything since midnight is enough, and cheap to read again
        var since = _runtime.NotBeforeEventStart(GameTime.FromGameTime(DateTimeOffset.UtcNow.ToGameTime().Date));
        int checkedCount = 0, added = 0, reviews = 0;
        var errors = new List<string>();

        foreach (var group in groups)
        {
            IReadOnlyList<Contracts.Slack.SlackEventItemDto> messages;
            try
            {
                messages = await history.GetChannelMessagesAsync(group.SlackChannelId, since, ct);
            }
            catch (Exception ex) when (ex is SlackApiException or HttpRequestException)
            {
                // One unreadable channel must not stop the others
                var error = ex is SlackApiException slack ? slack.Error : ex.Message;
                _logger.LogWarning(ex, "Catch-up could not read the channel of {Group}: {Error}", group.Name, error);
                errors.Add($"{group.Name}: {error}");
                continue;
            }

            foreach (var message in messages.Where(m => SlackRebuildService.IsCandidate(m, _runtime, botUserId)))
            {
                checkedCount++;
                var result = await workflow.ProcessMissedMessageAsync(message, ct);
                added += result.CreatedSubmissionIds.Count;

                if (await workflow.SyncReviewAsync(message.Channel!, message.Ts!, message.Reactions, ct))
                {
                    reviews++;
                }
            }
        }

        LastResult = new CatchUpResult(DateTimeOffset.UtcNow, checkedCount, added, reviews, errors);
        if (added > 0 || reviews > 0)
        {
            _logger.LogWarning("Catch-up found missed Slack activity: {Added} new submission(s), {Reviews} review(s) updated, {Checked} message(s) checked",
                added, reviews, checkedCount);
        }
        else
        {
            _logger.LogInformation("Catch-up done: nothing missed in {Checked} message(s)", checkedCount);
        }
    }
}
