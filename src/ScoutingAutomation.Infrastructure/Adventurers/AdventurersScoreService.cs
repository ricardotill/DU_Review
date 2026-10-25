using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Everything the Avonturiers pages show, read in one go.</summary>
/// <param name="Submissions">Check-ins and themes, newest first.</param>
/// <param name="Unlocks">Locations unlocked per group on the dashboard.</param>
public sealed record AdventurersOverview(
    IReadOnlyList<Group> Groups,
    AdventurersStanding Standing,
    IReadOnlyList<Submission> Submissions,
    IReadOnlyList<Location> Locations,
    IReadOnlyDictionary<Guid, IReadOnlySet<int>> Unlocks,
    IReadOnlyList<string> UnlockPool,
    DateTimeOffset Now)
{
    public ThemeRound? RunningRound => Standing.Rounds.FirstOrDefault(r => r.IsRunning)?.Round;

    public ThemeRound? NextRound => Standing.Rounds.Where(r => r.Round.Start > Now).OrderBy(r => r.Round.Start).FirstOrDefault()?.Round;

    /// <summary>The total of unlockable assignments: the pool, or else the number of locations; null when neither is set.</summary>
    public int? UnlockTotal => UnlockPool.Count > 0 ? UnlockPool.Count : Locations.Count > 0 ? Locations.Count : null;

    public IReadOnlySet<int> UnlocksOf(Guid groupId) => Unlocks.GetValueOrDefault(groupId) ?? new HashSet<int>();
}

/// <summary>Reads the Avonturiers data and computes the standing; nothing of it is stored.</summary>
public sealed class AdventurersScoreService
{
    private readonly ScoutingDbContext _context;
    private readonly RuntimeSettingsService _runtime;
    private readonly TimeProvider _time;

    public AdventurersScoreService(ScoutingDbContext context, RuntimeSettingsService runtime, TimeProvider time)
    {
        _context = context;
        _runtime = runtime;
        _time = time;
    }

    public async Task<AdventurersOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        var groups = await _context.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);
        var submissions = await _context.Submissions
            .Include(s => s.Group)
            .Include(s => s.Reviews)
            .AsNoTracking()
            .Where(s => s.Kind != SubmissionKind.Assignment)
            .ToListAsync(ct);
        var options = _runtime.AdventurersOptions;
        var final = (await _context.GroupBonusPoints.AsNoTracking().Where(p => p.CategoryKey == options.FinalGameCategoryKey).ToListAsync(ct))
            .ToDictionary(p => p.GroupId, p => p.Points);
        var locations = await _context.Locations.AsNoTracking().OrderBy(l => l.Number).ToListAsync(ct);
        var unlocks = (await _context.LocationUnlocks.AsNoTracking().ToListAsync(ct))
            .GroupBy(u => u.GroupId)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(u => u.LocationNumber).ToHashSet());

        var now = _time.GetUtcNow();
        var standing = AdventurersScoring.Compute(groups.Select(g => g.Id), submissions, _runtime.ThemeRounds, final,
            options.ThemeMaxPoints, options.FinalMaxPoints, now);
        return new AdventurersOverview(groups, standing, submissions.OrderByDescending(s => s.CreatedAt).ToList(), locations, unlocks, _runtime.UnlockPool, now);
    }
}
