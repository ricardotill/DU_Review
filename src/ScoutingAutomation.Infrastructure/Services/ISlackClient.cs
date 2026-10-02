using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

public interface ISlackClient
{
    Task<Stream> DownloadFileAsync(string fileUrl, string? fileName = null, CancellationToken ct = default);
    /// <returns>False when the bot had already placed this reaction, otherwise true.</returns>
    Task<bool> AddReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default);
    /// <summary>Takes back a reaction the bot placed. Does nothing when it is not there.</summary>
    Task RemoveReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default);

    Task PostThreadReplyAsync(string channel, string threadTs, string text, CancellationToken ct = default);

    /// <summary>Posts a new message in a channel. Never retried, so it cannot be posted twice.</summary>
    /// <returns>Null when posted, otherwise Slack's error code ("simulation_mode" when calls do not reach Slack).</returns>
    Task<string?> PostMessageAsync(string channel, string text, CancellationToken ct = default);

    /// <summary>
    /// Uploads a file and shares it in a channel as a new message, with <paramref name="text"/> above it.
    /// Never retried, so it cannot be posted twice.
    /// </summary>
    /// <returns>Null when posted, otherwise Slack's error code ("simulation_mode" when calls do not reach Slack).</returns>
    Task<string?> PostFileAsync(string channel, byte[] content, string fileName, string? text, CancellationToken ct = default);

    /// <summary>The reactions currently on a message.</summary>
    /// <returns>Null when they cannot be read: in simulation mode, or when Slack refuses.</returns>
    Task<IReadOnlyList<SlackMessageReactionDto>?> GetReactionsAsync(string channel, string messageTs, CancellationToken ct = default);

    /// <summary>Whether calls reach Slack: false in simulation mode or without a bot token.</summary>
    bool IsConnected { get; }

    /// <summary>A Slack user's display name, or their full name when they set no display name.</summary>
    Task<SlackUserLookup> GetUserNameAsync(string userId, CancellationToken ct = default);

    /// <summary>The bot's workspace, for links to messages, and the bot's own user ID.</summary>
    /// <returns>Null when it cannot be read: in simulation mode, or when Slack refuses.</returns>
    Task<SlackWorkspace?> GetWorkspaceAsync(CancellationToken ct = default);
}

/// <param name="TeamId">The workspace ID, e.g. "T012AB3C4".</param>
/// <param name="Url">The workspace's web address, ending in a slash, e.g. "https://scouting.slack.com/".</param>
/// <param name="BotUserId">The bot's own user ID, e.g. "U0BOT"; null when Slack did not say.</param>
public sealed record SlackWorkspace(string TeamId, string Url, string? BotUserId = null);

/// <param name="Name">The name, or null when it could not be found.</param>
/// <param name="Error">Slack's error code (such as "user_not_found"), or null on success.</param>
public sealed record SlackUserLookup(string? Name, string? Error)
{
    public static SlackUserLookup Found(string name) => new(name, null);
    public static SlackUserLookup Failed(string error) => new(null, error);
}
