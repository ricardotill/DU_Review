using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class AssignmentImageRepository : IAssignmentImageRepository
{
    private readonly ScoutingDbContext _context;

    public AssignmentImageRepository(ScoutingDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<AssignmentImage>> GetForAssignmentAsync(string code, CancellationToken ct = default) =>
        Ordered(await _context.AssignmentImages.AsNoTracking().Where(i => i.AssignmentCode == code).ToListAsync(ct));

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<AssignmentImage>>> GetAllAsync(CancellationToken ct = default) =>
        (await _context.AssignmentImages.AsNoTracking().ToListAsync(ct))
            .GroupBy(i => i.AssignmentCode)
            .ToDictionary(g => g.Key, g => Ordered(g));

    public async Task<AssignmentImage?> GetAsync(Guid id, CancellationToken ct = default) =>
        await _context.AssignmentImages.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task AddAsync(AssignmentImage image, CancellationToken ct = default)
    {
        _context.AssignmentImages.Add(image);
        await _context.SaveChangesAsync(ct);
    }

    public async Task<AssignmentImage?> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var image = await _context.AssignmentImages.FirstOrDefaultAsync(i => i.Id == id, ct);
        if (image == null) return null;

        _context.AssignmentImages.Remove(image);
        await _context.SaveChangesAsync(ct);
        return image;
    }

    public async Task ReorderAsync(string code, IReadOnlyList<Guid> order, CancellationToken ct = default)
    {
        var images = Ordered(await _context.AssignmentImages.Where(i => i.AssignmentCode == code).ToListAsync(ct));
        var listed = order.Select(id => images.FirstOrDefault(i => i.Id == id)).OfType<AssignmentImage>().Distinct();
        var position = 0;
        foreach (var image in listed.Concat(images.Where(i => !order.Contains(i.Id))))
        {
            image.SortOrder = position++;
        }

        await _context.SaveChangesAsync(ct);
    }

    // Sorted here: SQLite cannot order by a DateTimeOffset
    private static IReadOnlyList<AssignmentImage> Ordered(IEnumerable<AssignmentImage> images) =>
        images.OrderBy(i => i.SortOrder).ThenBy(i => i.UploadedAt).ToList();
}
