using System.Collections.Concurrent;

namespace ScoutingAutomation.Infrastructure.Slack;

/// <summary>
/// The Slack file IDs of pictures the app uploaded itself. Slack sends the bot's own upload back as a message;
/// the bot's user ID tells it apart, but that is not known until Slack answered once. The file ID always is.
/// </summary>
public sealed class SlackOwnUploads
{
    private readonly ConcurrentDictionary<string, byte> _fileIds = new();

    public void Add(string fileId) => _fileIds.TryAdd(fileId, 0);

    public bool Contains(string fileId) => _fileIds.ContainsKey(fileId);
}
