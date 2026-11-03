using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Which Toeristen assignments a group can do, and why.</summary>
/// <param name="OpenLocations">Locations 1 through this number are open for the group.</param>
/// <param name="ApprovedLocations">Distinct locations with an approved check-in, lowest first; also those not open yet.</param>
/// <param name="CountedLocations">The approved locations that are open for the group: these open assignments.</param>
/// <param name="OpenCount">Numbered assignments open, the lowest numbers.</param>
/// <param name="Total">All numbered assignments.</param>
/// <param name="OpenCodes">The open assignments: the lowest <see cref="OpenCount"/> numbers, then every letter assignment.</param>
/// <param name="NextLocationOpens">How many more assignments the next counted location opens.</param>
/// <param name="NextSuperLocationOpens">How many more the next counted super location opens; 0 without super locations left.</param>
/// <param name="WaitingLocations">Approved, open locations whose check-in waits for the end of its round to count.</param>
/// <param name="NextReleaseAt">When the first waiting location starts to count; null when none waits.</param>
public sealed record UnlockState(
    Guid GroupId,
    int OpenLocations,
    IReadOnlyList<string> ApprovedLocations,
    IReadOnlyList<string> CountedLocations,
    int OpenCount,
    int Total,
    IReadOnlyList<string> OpenCodes,
    int NextLocationOpens,
    IReadOnlyList<string> WaitingLocations,
    DateTimeOffset? NextReleaseAt,
    int NextSuperLocationOpens = 0)
{
    private readonly HashSet<string> _open = OpenCodes.ToHashSet();

    /// <summary>Whether the group can do the assignment (a normalised code). Letter assignments are never locked.</summary>
    public bool IsOpen(string code) => AssignmentCode.IsLetter(code) || _open.Contains(code);

    /// <summary>Whether the location is open for the group.</summary>
    public bool IsLocationOpen(int number) => number <= OpenLocations;

    /// <summary>Everything open: the Avonturiers part is off, so nothing is ever locked.</summary>
    public static UnlockState AllOpen(Guid groupId, IReadOnlyList<string> codes)
    {
        var numbered = codes.Count(c => !AssignmentCode.IsLetter(c));
        return new UnlockState(groupId, int.MaxValue, Array.Empty<string>(), Array.Empty<string>(), numbered, numbered, codes, 0, Array.Empty<string>(), null);
    }
}

/// <summary>
/// The unlock rule, computed from the check-ins every time and never stored, so a late review, a rebuild or a location
/// opened later always gives the right result. The lowest <c>initial</c> numbered assignments are open from the start;
/// the rest open in proportion to the locations: with k of the L locations counted, the first
/// <c>initial + (all - initial) * k / L</c> (rounded down) are open. With 30 of 160 open at the start and 100 locations,
/// each location opens one or two, and all 100 open all 160.
/// A super location ('supercoördinaat') weighs two: k and L count it twice, so it opens twice as many and all
/// locations together still open everything.
/// A check-in counts from the end of the first theme round that ends after it was posted on Slack (see
/// <see cref="ReleaseAt"/>), so the assignments open per round; when no round ends after it, it counts at once.
/// </summary>
public static class AssignmentUnlocks
{
    public static UnlockState Compute(
        Guid groupId,
        IEnumerable<Submission> checkIns,
        int openLocations,
        IReadOnlyCollection<int> locationNumbers,
        IEnumerable<string> assignmentCodes,
        int initialOpen,
        IReadOnlyList<ThemeRound>? rounds = null,
        DateTimeOffset? now = null,
        IReadOnlyCollection<int>? superLocations = null)
    {
        var codes = assignmentCodes.Distinct().OrderBy(c => c, AssignmentCode.Comparer).ToList();
        var numbered = codes.Where(c => !AssignmentCode.IsLetter(c)).ToList();
        var letters = codes.Where(AssignmentCode.IsLetter).ToList();

        var all = checkIns.ToList();
        rounds ??= Array.Empty<ThemeRound>();
        var released = now is { } at ? all.Where(c => ReleaseAt(c.CreatedAt, rounds) <= at).ToList() : all;

        var approved = AdventurersScoring.ApprovedLocations(all);
        var counted = CountedLocations(AdventurersScoring.ApprovedLocations(released), openLocations, locationNumbers);
        var waiting = CountedLocations(approved, openLocations, locationNumbers).Except(counted).ToList();
        var nextRelease = all
            .Where(c => c.AssignmentCode != null && waiting.Contains(c.AssignmentCode) && AdventurersScoring.IsApproved(c.Status))
            .Select(c => ReleaseAt(c.CreatedAt, rounds))
            .Order()
            .Select(t => (DateTimeOffset?)t)
            .FirstOrDefault();
        // A super number that is no location weighs nothing: only counted locations and the location list are weighed
        superLocations ??= Array.Empty<int>();
        var weight = Weight(counted, superLocations);
        var total = Weight(locationNumbers.Select(n => n.ToString()), superLocations);
        var open = OpenCount(initialOpen, numbered.Count, weight, total);

        // What the group can still visit now: open for it, and no approved check-in yet (also not one waiting for its round)
        var visited = approved.Select(c => int.TryParse(c, out var n) ? n : 0).ToHashSet();
        var left = locationNumbers.Where(n => n <= openLocations && !visited.Contains(n)).ToList();
        var next = left.Any(n => !superLocations.Contains(n)) ? OpenCount(initialOpen, numbered.Count, weight + 1, total) - open : 0;
        var nextSuper = left.Any(superLocations.Contains) ? OpenCount(initialOpen, numbered.Count, weight + SuperWeight, total) - open : 0;

        return new UnlockState(groupId, openLocations, approved, counted, open, numbered.Count, numbered.Take(open).Concat(letters).ToList(), next,
            waiting, nextRelease, nextSuper);
    }

    /// <summary>What a super location counts as.</summary>
    public const int SuperWeight = 2;

    /// <summary>The counted locations with each super location twice.</summary>
    public static int Weight(IEnumerable<string> locations, IReadOnlyCollection<int> superLocations) =>
        locations.Sum(c => int.TryParse(c, out var n) && superLocations.Contains(n) ? SuperWeight : 1);

    /// <summary>
    /// When a check-in posted at <paramref name="postedAt"/> starts to count: at the end of the first round that ends
    /// after it (also when it was posted before or between rounds), or at once when no round ends after it.
    /// </summary>
    public static DateTimeOffset ReleaseAt(DateTimeOffset postedAt, IReadOnlyList<ThemeRound> rounds)
    {
        DateTimeOffset? end = null;
        foreach (var round in rounds)
        {
            if (round.End > postedAt && (end == null || round.End < end)) end = round.End;
        }

        return end ?? postedAt;
    }

    /// <summary>
    /// The approved locations that count: open for the group, and still in the list of locations. Without any
    /// locations set up every approved number counts, like every number is accepted then.
    /// </summary>
    public static IReadOnlyList<string> CountedLocations(IReadOnlyList<string> approved, int openLocations, IReadOnlyCollection<int> locationNumbers) =>
        approved
            .Where(c => int.TryParse(c, out var n) && n <= openLocations && (locationNumbers.Count == 0 || locationNumbers.Contains(n)))
            .ToList();

    /// <summary>
    /// How many numbered assignments are open with <paramref name="counted"/> of <paramref name="locations"/> locations.
    /// Without locations nothing can be unlocked, so everything is open.
    /// </summary>
    public static int OpenCount(int initialOpen, int numbered, int counted, int locations)
    {
        if (numbered <= 0) return 0;
        if (locations <= 0) return numbered;

        var start = Math.Clamp(initialOpen, 0, numbered);
        var k = Math.Clamp(counted, 0, locations);
        return start + (int)((long)(numbered - start) * k / locations);
    }

    /// <summary>The assignments that opened going from <paramref name="previous"/> to <paramref name="count"/> open ones.</summary>
    public static IReadOnlyList<string> NewlyOpened(int previous, int count, IEnumerable<string> assignmentCodes)
    {
        if (count <= previous) return Array.Empty<string>();

        return assignmentCodes
            .Where(c => !AssignmentCode.IsLetter(c))
            .Distinct()
            .OrderBy(c => c, AssignmentCode.Comparer)
            .Skip(Math.Max(0, previous))
            .Take(count - Math.Max(0, previous))
            .ToList();
    }
}
