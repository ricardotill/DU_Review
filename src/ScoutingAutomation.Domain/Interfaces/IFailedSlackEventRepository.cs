using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IFailedSlackEventRepository
{
    /// <summary>Adds the failure, or counts one more attempt on the row with the same key and opens it again.</summary>
    Task RecordFailureAsync(string eventKey, string kind, string? channelId, string? messageTs, string payloadJson, string error, CancellationToken ct = default);

    /// <summary>Marks the open row with this key as resolved. Does nothing when there is none.</summary>
    Task MarkResolvedAsync(string eventKey, CancellationToken ct = default);

    Task<FailedSlackEvent?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Open failures, oldest first: the order they happened in Slack.</summary>
    Task<IReadOnlyList<FailedSlackEvent>> GetPendingAsync(CancellationToken ct = default);

    Task DismissAsync(Guid id, CancellationToken ct = default);
}
