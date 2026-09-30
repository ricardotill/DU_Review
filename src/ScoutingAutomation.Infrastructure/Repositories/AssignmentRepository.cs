using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class AssignmentRepository : IAssignmentRepository
{
    private readonly ScoutingDbContext _context;

    public AssignmentRepository(ScoutingDbContext context)
    {
        _context = context;
    }

    public async Task<Assignment?> GetByNumberAsync(int number, CancellationToken ct = default)
    {
        return await _context.Assignments.FirstOrDefaultAsync(a => a.Number == number, ct);
    }

    public async Task<IReadOnlyList<Assignment>> GetAllAsync(CancellationToken ct = default)
    {
        return await _context.Assignments
            .OrderBy(a => a.Number)
            .ToListAsync(ct);
    }

    public async Task UpsertAsync(Assignment assignment, CancellationToken ct = default)
    {
        var existing = await _context.Assignments.FirstOrDefaultAsync(a => a.Number == assignment.Number, ct);
        if (existing == null)
        {
            await _context.Assignments.AddAsync(assignment, ct);
        }
        else
        {
            existing.StandardPoints = assignment.StandardPoints;
            existing.AwesomePoints = assignment.AwesomePoints;
        }

        await _context.SaveChangesAsync(ct);
    }
}
