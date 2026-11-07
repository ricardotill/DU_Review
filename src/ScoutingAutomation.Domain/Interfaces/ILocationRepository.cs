using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

/// <summary>The Avonturiers locations and how many of them each group has open.</summary>
public interface ILocationRepository
{
    /// <summary>All locations, by number.</summary>
    Task<IReadOnlyList<Location>> GetAllAsync(CancellationToken ct = default);

    Task<Location?> GetAsync(int number, CancellationToken ct = default);

    /// <summary>Adds the location, or replaces the one with the same number.</summary>
    Task UpsertAsync(Location location, CancellationToken ct = default);

    /// <summary>Adds or replaces several locations at once, e.g. from an imported file.</summary>
    Task UpsertManyAsync(IReadOnlyCollection<Location> locations, CancellationToken ct = default);

    /// <summary>Removes the location. Check-ins already made keep their number.</summary>
    Task DeleteAsync(int number, CancellationToken ct = default);

    /// <summary>Opens locations 1 through <paramref name="openLocations"/> for the group; null goes back to the number every group starts with.</summary>
    /// <returns>True when something changed.</returns>
    Task<bool> SetOpenLocationsAsync(Guid groupId, int? openLocations, CancellationToken ct = default);
}
