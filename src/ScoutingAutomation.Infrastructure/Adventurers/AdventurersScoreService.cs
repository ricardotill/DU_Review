using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Everything the Avonturiers pages show, read in one go.</summary>
/// <param name="Submissions">Check-ins and themes, newest first.</param>
/// <param name="Unlocks">Per group: its open locations and the assignments its Avonturiers opened.</param>
public sealed record AdventurersOverview(
    IReadOnlyList<Group> Groups,
    AdventurersStanding Standing,
    IReadOnlyList<Submission> Submissions,
    IReadOnlyList<Location> Locations,
    IReadOnlyDictionary<Guid, UnlockState> Unlocks,
    DateTimeOffset Now)
{
    public ThemeRound? RunningRound => Standing.Rounds.FirstOrDefault(r => r.IsRunning)?.Round;

    public ThemeRound? NextRound => Standing.Rounds.Where(r => r.Round.Start > Now).OrderBy(r => r.Round.Start).FirstOrDefault()?.Round;

    public UnlockState? UnlocksOf(Guid groupId) => Unlocks.GetValueOrDefault(groupId);

    /// <summary>Whether the location is open for the group; true for a group that is not known.</summary>
    public bool IsLocationOpen(Guid groupId, int number) => UnlocksOf(groupId)?.IsLocationOpen(number) ?? true;
}

/// <summary>Reads the Avonturiers data and computes the standing; nothing of it is stored.</summary>
public sealed class AdventurersScoreService
{
    private readonly ScoutingDbContext _context;
    private readonly RuntimeSettingsService _runtime;
    private readonly TimeProvider _time;
    private readonly AssignmentUnlockService _unlocks;

    public AdventurersScoreService(ScoutingDbContext context, RuntimeSettingsService runtime, TimeProvider time, AssignmentUnlockService unlocks)
    {
        _context = context;
        _runtime = runtime;
        _time = time;
        _unlocks = unlocks;
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
        var unlocks = await _unlocks.ForAllAsync(ct);

        var now = _time.GetUtcNow();
        var standing = AdventurersScoring.Compute(groups.Select(g => g.Id), submissions, _runtime.ThemeRounds, final,
            _runtime.ThemeMaxPoints, _runtime.FinalMaxPoints, now);
        return new AdventurersOverview(groups, standing, submissions.OrderByDescending(s => s.CreatedAt).ToList(), locations, unlocks, now);
    }
}
