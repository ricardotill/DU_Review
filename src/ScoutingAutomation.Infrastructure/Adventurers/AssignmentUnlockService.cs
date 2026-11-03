using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Reads what the unlock rule needs and computes, per group, which Toeristen assignments are open.</summary>
public sealed class AssignmentUnlockService
{
    private readonly IGroupRepository _groups;
    private readonly ILocationRepository _locations;
    private readonly IAssignmentRepository _assignments;
    private readonly ISubmissionRepository _submissions;
    private readonly RuntimeSettingsService _runtime;
    private readonly TimeProvider _time;

    public AssignmentUnlockService(
        IGroupRepository groups,
        ILocationRepository locations,
        IAssignmentRepository assignments,
        ISubmissionRepository submissions,
        RuntimeSettingsService runtime,
        TimeProvider? time = null)
    {
        _groups = groups;
        _locations = locations;
        _assignments = assignments;
        _submissions = submissions;
        _runtime = runtime;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Locations 1 through this number are open for the group.</summary>
    public int OpenLocationsOf(Group group) => group.OpenLocations ?? _runtime.InitialOpenLocations;

    /// <summary>The state of one group; null when the group does not exist.</summary>
    public async Task<UnlockState?> ForGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        var group = await _groups.GetByIdAsync(groupId, ct);
        if (group == null) return null;

        var codes = await CodesAsync(ct);
        if (!_runtime.AdventurersEnabled) return UnlockState.AllOpen(groupId, codes);

        var numbers = await LocationNumbersAsync(ct);
        var checkIns = await _submissions.SearchSubmissionsAsync(new SubmissionFilter { GroupId = groupId, Kind = SubmissionKind.CheckIn }, ct);
        return AssignmentUnlocks.Compute(groupId, checkIns, OpenLocationsOf(group), numbers, codes, _runtime.InitialOpenAssignments, _runtime.ThemeRounds, _time.GetUtcNow(),
            _runtime.SuperLocations);
    }

    /// <summary>The state of every group.</summary>
    public async Task<IReadOnlyDictionary<Guid, UnlockState>> ForAllAsync(CancellationToken ct = default)
    {
        var groups = await _groups.GetAllAsync(ct);
        var codes = await CodesAsync(ct);
        if (!_runtime.AdventurersEnabled) return groups.ToDictionary(g => g.Id, g => UnlockState.AllOpen(g.Id, codes));

        var numbers = await LocationNumbersAsync(ct);
        var checkIns = (await _submissions.SearchSubmissionsAsync(new SubmissionFilter { Kind = SubmissionKind.CheckIn }, ct)).ToLookup(s => s.GroupId);
        var rounds = _runtime.ThemeRounds;
        var now = _time.GetUtcNow();
        var super = _runtime.SuperLocations;
        return groups.ToDictionary(
            g => g.Id,
            g => AssignmentUnlocks.Compute(g.Id, checkIns[g.Id], OpenLocationsOf(g), numbers, codes, _runtime.InitialOpenAssignments, rounds, now, super));
    }

    private async Task<IReadOnlyList<string>> CodesAsync(CancellationToken ct) =>
        (await _assignments.GetAllAsync(ct)).Select(a => a.Code).ToList();

    private async Task<IReadOnlyCollection<int>> LocationNumbersAsync(CancellationToken ct) =>
        (await _locations.GetAllAsync(ct)).Select(l => l.Number).ToHashSet();
}
