using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IGroupRepository
{
    Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<Group?> GetBySlackChannelIdAsync(string channelId, CancellationToken ct = default);

    /// <summary>The group a channel belongs to, as its Toeristen or its Avonturiers channel; null for any other channel.</summary>
    Task<GroupChannel?> GetByAnyChannelIdAsync(string channelId, CancellationToken ct = default);
    Task<Group?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Group>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(Group group, CancellationToken ct = default);

    /// <summary>The points entered by hand, every group and category, also of categories no longer in use.</summary>
    Task<IReadOnlyList<GroupBonusPoints>> GetBonusPointsAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the points entered by hand, all or nothing; the time of change is kept only for a category that changed.
    /// Throws <see cref="Exceptions.BonusPointsConflictException"/> when a group no longer has its previous values.
    /// </summary>
    Task SetBonusPointsAsync(IReadOnlyCollection<BonusPointsChange> changes, CancellationToken ct = default);
}
