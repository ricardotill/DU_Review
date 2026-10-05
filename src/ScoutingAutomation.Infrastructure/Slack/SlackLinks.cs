using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Slack;

/// <summary>
/// Links from the app to a Slack message: one that opens the Slack app, and the web permalink as a fallback.
/// The workspace is asked from Slack once; until that works (or in simulation) there are no links.
/// It also tells the bot's own user ID, so the bot's messages are never taken for submissions.
/// </summary>
public class SlackLinks
{
    /// <summary>Time to wait before asking Slack again after a failed attempt.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<SlackLinks> _logger;
    private readonly SemaphoreSlim _loading = new(1, 1);
    private SlackWorkspace? _workspace;
    private DateTimeOffset? _lastAttempt;

    public SlackLinks(IServiceScopeFactory scopeFactory, TimeProvider time, ILogger<SlackLinks> logger)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>The workspace, when known.</summary>
    public SlackWorkspace? Workspace => _workspace;

    /// <summary>The bot's own user ID, when known.</summary>
    public string? BotUserId => _workspace?.BotUserId;

    /// <summary>Asks Slack for the workspace when it is not known yet. Cheap to call often.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_workspace != null) return;
        if (_lastAttempt is { } last && _time.GetUtcNow() - last < RetryAfter) return;

        await _loading.WaitAsync(ct);
        try
        {
            if (_workspace != null) return;
            _lastAttempt = _time.GetUtcNow();

            await using var scope = _scopeFactory.CreateAsyncScope();
            var slack = scope.ServiceProvider.GetRequiredService<ISlackClient>();
            if (!slack.IsConnected) return;

            _workspace = await slack.GetWorkspaceAsync(ct);
            if (_workspace != null)
            {
                _logger.LogInformation("Slack workspace {TeamId} at {Url}", _workspace.TeamId, _workspace.Url);
            }
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>Opens the message in the Slack app; null when the workspace is unknown.</summary>
    public string? AppLink(string? channelId, string? messageTs) => AppLink(_workspace, channelId, messageTs);

    /// <summary>The message's permalink in the browser; null when the workspace is unknown.</summary>
    public string? WebLink(string? channelId, string? messageTs) => WebLink(_workspace, channelId, messageTs);

    public static string? AppLink(SlackWorkspace? workspace, string? channelId, string? messageTs) =>
        workspace == null || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageTs)
            ? null
            : $"slack://channel?team={Uri.EscapeDataString(workspace.TeamId)}&id={Uri.EscapeDataString(channelId)}&message={Uri.EscapeDataString(messageTs)}";

    // Slack's permalink is the timestamp without its dot, e.g. 1712345678.000100 becomes p1712345678000100
    public static string? WebLink(SlackWorkspace? workspace, string? channelId, string? messageTs) =>
        workspace == null || string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(messageTs)
            ? null
            : $"{workspace.Url}archives/{Uri.EscapeDataString(channelId)}/p{messageTs.Replace(".", "")}";
}
