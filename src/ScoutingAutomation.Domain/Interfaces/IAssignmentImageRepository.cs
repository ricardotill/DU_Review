using ScoutingAutomation.Domain.Entities;

namespace ScoutingAutomation.Domain.Interfaces;

/// <summary>The pictures of the assignments.</summary>
public interface IAssignmentImageRepository
{
    /// <summary>The pictures of one assignment, in their order.</summary>
    Task<IReadOnlyList<AssignmentImage>> GetForAssignmentAsync(string code, CancellationToken ct = default);

    /// <summary>The pictures of every assignment, by code, each in their order.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<AssignmentImage>>> GetAllAsync(CancellationToken ct = default);

    Task<AssignmentImage?> GetAsync(Guid id, CancellationToken ct = default);

    Task AddAsync(AssignmentImage image, CancellationToken ct = default);

    /// <returns>The removed picture; null when it was already gone.</returns>
    Task<AssignmentImage?> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Puts the pictures of an assignment in the given order; pictures not listed keep their place after them.</summary>
    Task ReorderAsync(string code, IReadOnlyList<Guid> order, CancellationToken ct = default);
}
