using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class GroupRepository : IGroupRepository
{
    private readonly ScoutingDbContext _context;
    private readonly TimeProvider _time;

    public GroupRepository(ScoutingDbContext context, TimeProvider? time = null)
    {
        _context = context;
        _time = time ?? TimeProvider.System;
    }

    public async Task<Group?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.Submissions)
            .FirstOrDefaultAsync(g => g.Id == id, ct);
    }

    public async Task<Group?> GetBySlackChannelIdAsync(string channelId, CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.Submissions)
            .FirstOrDefaultAsync(g => g.SlackChannelId == channelId, ct);
    }

    public async Task<Group?> GetByNameAsync(string name, CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.Submissions)
            .FirstOrDefaultAsync(g => g.Name.ToLower() == name.ToLower(), ct);
    }

    public async Task<IReadOnlyList<Group>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.Groups
            .Include(g => g.Submissions)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GroupBonusPoints>> GetBonusPointsAsync(CancellationToken ct = default)
    {
        return await _context.GroupBonusPoints.AsNoTracking().ToListAsync(ct);
    }

    // One save at a time: the check for someone else's change and the save must not interleave
    private static readonly SemaphoreSlim BonusSaves = new(1, 1);

    public async Task SetBonusPointsAsync(IReadOnlyCollection<BonusPointsChange> changes, CancellationToken ct = default)
    {
        if (changes.Any(c => !IsValidPoints(c.Points)))
        {
            throw new ArgumentOutOfRangeException(nameof(changes), $"Punten moeten tussen 0 en {BonusPointsChange.MaxPoints} liggen");
        }

        if (changes.Any(c => string.IsNullOrWhiteSpace(c.CategoryKey)))
        {
            throw new ArgumentException("Elke wijziging hoort bij een categorie", nameof(changes));
        }

        if (changes.Count == 0) return;

        await BonusSaves.WaitAsync(ct);
        try
        {
            var ids = changes.Select(c => c.GroupId).Distinct().ToList();
            var groups = await _context.Groups.Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, ct);
            var current = await _context.GroupBonusPoints
                .Where(p => ids.Contains(p.GroupId))
                .ToDictionaryAsync(p => (p.GroupId, p.CategoryKey), ct);

            // Everything is checked before anything changes: a save is all or nothing
            foreach (var change in changes)
            {
                if (!groups.TryGetValue(change.GroupId, out var group))
                {
                    throw new InvalidOperationException("Groep niet gevonden");
                }

                if ((current.GetValueOrDefault((change.GroupId, change.CategoryKey))?.Points ?? 0) != change.Previous)
                {
                    throw new BonusPointsConflictException(group.Name);
                }
            }

            var now = _time.GetUtcNow();
            var withHistory = (await _context.BonusPointsEntries
                    .Where(e => ids.Contains(e.GroupId))
                    .Select(e => new { e.GroupId, e.CategoryKey })
                    .Distinct()
                    .ToListAsync(ct))
                .Select(e => (e.GroupId, e.CategoryKey))
                .ToHashSet();

            foreach (var change in changes)
            {
                var row = current.GetValueOrDefault((change.GroupId, change.CategoryKey));
                var previous = row?.Points ?? 0;
                if (previous == change.Points) continue;

                AddHistory(groups[change.GroupId], change.CategoryKey, previous, row?.UpdatedAt, change.Points, now, withHistory);
                if (row == null)
                {
                    row = new GroupBonusPoints { GroupId = change.GroupId, CategoryKey = change.CategoryKey };
                    _context.GroupBonusPoints.Add(row);
                    current[(change.GroupId, change.CategoryKey)] = row;
                }

                row.Points = change.Points;
                row.UpdatedAt = now;
            }

            await _context.SaveChangesAsync(ct);
        }
        finally
        {
            BonusSaves.Release();
        }
    }

    // Saved together with the new value, so the history and the score can't drift apart
    private void AddHistory(Group group, string category, int previous, DateTimeOffset? previousAt, int points,
        DateTimeOffset at, HashSet<(Guid, string)> withHistory)
    {
        // A value entered before changes were kept: record it at its own time first, before this change overwrites that time
        if (withHistory.Add((group.Id, category)) && previous != 0)
        {
            _context.BonusPointsEntries.Add(new BonusPointsEntry
            {
                GroupId = group.Id, CategoryKey = category, Delta = previous, Total = previous, ChangedAt = previousAt ?? group.CreatedAt
            });
        }

        _context.BonusPointsEntries.Add(new BonusPointsEntry
        {
            GroupId = group.Id,
            CategoryKey = category,
            Delta = points - previous,
            Total = points,
            ChangedAt = at
        });
    }

    private static bool IsValidPoints(int points) => points is >= 0 and <= BonusPointsChange.MaxPoints;

    public async Task UpsertAsync(Group group, CancellationToken ct = default)
    {
        var existing = await _context.Groups
            .FirstOrDefaultAsync(g => g.SlackChannelId == group.SlackChannelId || g.Name.ToLower() == group.Name.ToLower(), ct);

        if (existing == null)
        {
            await _context.Groups.AddAsync(group, ct);
        }
        else
        {
            existing.Name = group.Name;
            existing.Abbreviation = group.Abbreviation;
            existing.SlackChannelId = group.SlackChannelId;
            existing.Description = group.Description;
        }

        await _context.SaveChangesAsync(ct);
    }
}
