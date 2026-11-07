using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class AssignmentRepository : IAssignmentRepository
{
    private readonly ScoutingDbContext _context;

    public AssignmentRepository(ScoutingDbContext context)
    {
        _context = context;
    }

    public async Task<Assignment?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        return await _context.Assignments.FirstOrDefaultAsync(a => a.Code == code, ct);
    }

    public async Task<IReadOnlyList<Assignment>> GetAllAsync(CancellationToken ct = default)
    {
        // Sorted here: the database sorts text codes as text, which puts 10 before 2
        var assignments = await _context.Assignments.ToListAsync(ct);
        return assignments.OrderBy(a => a.Code, AssignmentCode.Comparer).ToList();
    }

    public async Task UpsertAsync(Assignment assignment, CancellationToken ct = default)
    {
        var existing = await _context.Assignments.FirstOrDefaultAsync(a => a.Code == assignment.Code, ct);
        if (existing == null)
        {
            await _context.Assignments.AddAsync(assignment, ct);
        }
        else
        {
            existing.StandardPoints = assignment.StandardPoints;
            existing.AwesomePoints = assignment.AwesomePoints;
            existing.Title = assignment.Title;
            existing.Description = assignment.Description;
            existing.EditedAt = assignment.EditedAt;
        }

        await _context.SaveChangesAsync(ct);
    }
}
