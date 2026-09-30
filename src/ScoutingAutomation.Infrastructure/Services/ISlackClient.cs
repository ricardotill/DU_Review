namespace ScoutingAutomation.Infrastructure.Services;

public interface ISlackClient
{
    Task<Stream> DownloadFileAsync(string fileUrl, string? fileName = null, CancellationToken ct = default);
    /// <returns>False when the bot had already placed this reaction, otherwise true.</returns>
    Task<bool> AddReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default);
    Task PostThreadReplyAsync(string channel, string threadTs, string text, CancellationToken ct = default);
}
