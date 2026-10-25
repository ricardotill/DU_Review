using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class LocationRepository : ILocationRepository
{
    private readonly ScoutingDbContext _context;

    public LocationRepository(ScoutingDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<Location>> GetAllAsync(CancellationToken ct = default) =>
        await _context.Locations.AsNoTracking().OrderBy(l => l.Number).ToListAsync(ct);

    public async Task<Location?> GetAsync(int number, CancellationToken ct = default) =>
        await _context.Locations.AsNoTracking().FirstOrDefaultAsync(l => l.Number == number, ct);

    public Task UpsertAsync(Location location, CancellationToken ct = default) => UpsertManyAsync(new[] { location }, ct);

    public async Task UpsertManyAsync(IReadOnlyCollection<Location> locations, CancellationToken ct = default)
    {
        if (locations.Any(l => l.Number <= 0)) throw new ArgumentException("Een locatienummer is 1 of hoger", nameof(locations));

        var numbers = locations.Select(l => l.Number).ToList();
        var existing = await _context.Locations.Where(l => numbers.Contains(l.Number)).ToDictionaryAsync(l => l.Number, ct);
        foreach (var location in locations)
        {
            if (existing.TryGetValue(location.Number, out var row))
            {
                row.Name = location.Name;
                row.Latitude = location.Latitude;
                row.Longitude = location.Longitude;
                row.Hint = location.Hint;
            }
            else
            {
                var added = new Location
                {
                    Number = location.Number, Name = location.Name, Latitude = location.Latitude, Longitude = location.Longitude, Hint = location.Hint
                };
                _context.Locations.Add(added);
                existing[location.Number] = added;
            }
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int number, CancellationToken ct = default)
    {
        _context.LocationUnlocks.RemoveRange(await _context.LocationUnlocks.Where(u => u.LocationNumber == number).ToListAsync(ct));
        _context.Locations.RemoveRange(await _context.Locations.Where(l => l.Number == number).ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<LocationUnlock>> GetUnlocksAsync(Guid? groupId = null, CancellationToken ct = default)
    {
        var query = _context.LocationUnlocks.AsNoTracking();
        if (groupId.HasValue) query = query.Where(u => u.GroupId == groupId.Value);
        return (await query.ToListAsync(ct)).OrderBy(u => u.LocationNumber).ToList();
    }

    public async Task<bool> SetUnlockedAsync(Guid groupId, int number, bool unlocked, DateTimeOffset at, CancellationToken ct = default)
    {
        var row = await _context.LocationUnlocks.FirstOrDefaultAsync(u => u.GroupId == groupId && u.LocationNumber == number, ct);
        if (unlocked == (row != null)) return false;

        if (unlocked)
        {
            _context.LocationUnlocks.Add(new LocationUnlock { GroupId = groupId, LocationNumber = number, UnlockedAt = at });
        }
        else
        {
            _context.LocationUnlocks.Remove(row!);
        }

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Unlocked at the same moment in another tab: the result is the same
            _context.ChangeTracker.Clear();
            return false;
        }

        return true;
    }
}
