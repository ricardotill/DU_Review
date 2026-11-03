using System.Collections.Concurrent;
using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Slack;

/// <summary>
/// The Slack file IDs of pictures the app uploaded itself. Slack sends the bot's own upload back as a message;
/// the bot's user ID tells it apart, but that is not known until Slack answered once. The file ID always is.
/// An upload from one instance is told to the others, because the event may reach another instance.
/// </summary>
public sealed class SlackOwnUploads
{
    public const string ClusterTopic = "own-upload";

    private readonly ConcurrentDictionary<string, byte> _fileIds = new();
    private readonly IClusterBus _bus;

    public SlackOwnUploads(IClusterBus? bus = null)
    {
        _bus = bus ?? SingleInstance.Instance;
        _bus.Subscribe(ClusterTopic, fileId => _fileIds.TryAdd(fileId, 0));
    }

    public void Add(string fileId)
    {
        if (_fileIds.TryAdd(fileId, 0)) _bus.Publish(ClusterTopic, fileId);
    }

    public bool Contains(string fileId) => _fileIds.ContainsKey(fileId);
}
