using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

public interface IAssignmentRepository
{
    /// <param name="code">A normalised code, see <see cref="Models.AssignmentCode"/>.</param>
    Task<Assignment?> GetByCodeAsync(string code, CancellationToken ct = default);
    /// <summary>All assignments, numbers first, then letter codes.</summary>
    Task<IReadOnlyList<Assignment>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(Assignment assignment, CancellationToken ct = default);
}
