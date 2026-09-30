using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

public interface ISlackClient
{
    Task<Stream> DownloadFileAsync(string fileUrl, string? fileName = null, CancellationToken ct = default);
    /// <returns>False when the bot had already placed this reaction, otherwise true.</returns>
    Task<bool> AddReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default);
    Task PostThreadReplyAsync(string channel, string threadTs, string text, CancellationToken ct = default);

    /// <summary>The reactions currently on a message.</summary>
    /// <returns>Null when they cannot be read: in simulation mode, or when Slack refuses.</returns>
    Task<IReadOnlyList<SlackMessageReactionDto>?> GetReactionsAsync(string channel, string messageTs, CancellationToken ct = default);

    /// <summary>Whether calls reach Slack: false in simulation mode or without a bot token.</summary>
    bool IsConnected { get; }

    /// <summary>A Slack user's display name, or their full name when they set no display name.</summary>
    Task<SlackUserLookup> GetUserNameAsync(string userId, CancellationToken ct = default);
}

/// <param name="Name">The name, or null when it could not be found.</param>
/// <param name="Error">Slack's error code (such as "user_not_found"), or null on success.</param>
public sealed record SlackUserLookup(string? Name, string? Error)
{
    public static SlackUserLookup Found(string name) => new(name, null);
    public static SlackUserLookup Failed(string error) => new(null, error);
}
