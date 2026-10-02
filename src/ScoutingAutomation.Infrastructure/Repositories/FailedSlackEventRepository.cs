using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class FailedSlackEventRepository : IFailedSlackEventRepository
{
    public const int MaxErrorLength = 2000;

    private readonly ScoutingDbContext _context;
    private readonly TimeProvider _time;

    public FailedSlackEventRepository(ScoutingDbContext context, TimeProvider time)
    {
        _context = context;
        _time = time;
    }

    public async Task RecordFailureAsync(string eventKey, string kind, string? channelId, string? messageTs, string payloadJson, string error, CancellationToken ct = default)
    {
        try
        {
            await RecordOnceAsync(eventKey, kind, channelId, messageTs, payloadJson, error, ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // The same event failed at the same moment elsewhere, e.g. Slack's retry of a slow webhook: count it on that row
            _context.ChangeTracker.Clear();
            await RecordOnceAsync(eventKey, kind, channelId, messageTs, payloadJson, error, ct);
        }
    }

    private async Task RecordOnceAsync(string eventKey, string kind, string? channelId, string? messageTs, string payloadJson, string error, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var existing = await FindAsync(e => e.EventKey == eventKey, ct);
        if (existing == null)
        {
            existing = new FailedSlackEvent
            {
                Id = Guid.NewGuid(),
                EventKey = eventKey,
                Kind = kind,
                ChannelId = channelId,
                MessageTs = messageTs,
                CreatedAt = now
            };
            _context.FailedSlackEvents.Add(existing);
        }

        existing.PayloadJson = payloadJson;
        existing.Error = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;
        existing.Attempts++;
        existing.Status = FailedSlackEventStatus.Pending;
        existing.UpdatedAt = now;
        await _context.SaveChangesAsync(ct);
    }

    public async Task MarkResolvedAsync(string eventKey, CancellationToken ct = default)
    {
        // Runs after every event that went through: almost always there is nothing open, and one indexed lookup says so
        if (!await _context.FailedSlackEvents.AnyAsync(e => e.EventKey == eventKey && e.Status == FailedSlackEventStatus.Pending, ct)) return;

        var existing = await FindAsync(e => e.EventKey == eventKey, ct);
        if (existing is not { Status: FailedSlackEventStatus.Pending }) return;

        existing.Status = FailedSlackEventStatus.Resolved;
        existing.UpdatedAt = _time.GetUtcNow();
        await _context.SaveChangesAsync(ct);
    }

    public Task<FailedSlackEvent?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        _context.FailedSlackEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<IReadOnlyList<FailedSlackEvent>> GetPendingAsync(CancellationToken ct = default)
    {
        var pending = await _context.FailedSlackEvents
            .AsNoTracking()
            .Where(e => e.Status == FailedSlackEventStatus.Pending)
            .ToListAsync(ct);

        // SQLite cannot sort on a DateTimeOffset
        return pending.OrderBy(e => e.CreatedAt).ToList();
    }

    /// <summary>
    /// The dispatcher writes these rows from its own scope, so a row this context tracks may be out of date: read it again.
    /// </summary>
    private async Task<FailedSlackEvent?> FindAsync(System.Linq.Expressions.Expression<Func<FailedSlackEvent, bool>> match, CancellationToken ct)
    {
        var row = await _context.FailedSlackEvents.FirstOrDefaultAsync(match, ct);
        if (row != null) await _context.Entry(row).ReloadAsync(ct);
        return row;
    }

    public async Task DismissAsync(Guid id, CancellationToken ct = default)
    {
        var existing = await FindAsync(e => e.Id == id, ct);
        if (existing == null) return;

        existing.Status = FailedSlackEventStatus.Dismissed;
        existing.UpdatedAt = _time.GetUtcNow();
        await _context.SaveChangesAsync(ct);
    }
}
