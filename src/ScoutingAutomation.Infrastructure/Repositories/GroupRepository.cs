using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class GroupRepository : IGroupRepository
{
    private readonly ScoutingDbContext _context;

    public GroupRepository(ScoutingDbContext context)
    {
        _context = context;
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
