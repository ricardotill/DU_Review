using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IGroupRepository
{
    Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Group?> GetBySlackChannelIdAsync(string channelId, CancellationToken ct = default);
    Task<Group?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Group>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(Group group, CancellationToken ct = default);
}
