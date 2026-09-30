using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IAssignmentRepository
{
    Task<Assignment?> GetByNumberAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<Assignment>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(Assignment assignment, CancellationToken ct = default);
}
