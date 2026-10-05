using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

/// <summary>The Avonturiers locations and which groups unlocked which.</summary>
public interface ILocationRepository
{
    /// <summary>All locations, by number.</summary>
    Task<IReadOnlyList<Location>> GetAllAsync(CancellationToken ct = default);

    Task<Location?> GetAsync(int number, CancellationToken ct = default);

    /// <summary>Adds the location, or replaces the one with the same number.</summary>
    Task UpsertAsync(Location location, CancellationToken ct = default);

    /// <summary>Adds or replaces several locations at once, e.g. from an imported file.</summary>
    Task UpsertManyAsync(IReadOnlyCollection<Location> locations, CancellationToken ct = default);

    /// <summary>Removes the location and its unlocks. Check-ins already made keep their number.</summary>
    Task DeleteAsync(int number, CancellationToken ct = default);

    /// <summary>Unlocks of one group, or of every group when <paramref name="groupId"/> is null.</summary>
    Task<IReadOnlyList<LocationUnlock>> GetUnlocksAsync(Guid? groupId = null, CancellationToken ct = default);

    /// <summary>Unlocks or locks one location for one group. Does nothing when it already is so.</summary>
    /// <returns>True when something changed.</returns>
    Task<bool> SetUnlockedAsync(Guid groupId, int number, bool unlocked, DateTimeOffset at, CancellationToken ct = default);
}
