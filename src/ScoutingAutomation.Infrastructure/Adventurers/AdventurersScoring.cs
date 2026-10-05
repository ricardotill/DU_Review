using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>A group's place in one round. Equal counts share a place, and the next place is skipped (1, 1, 3).</summary>
/// <param name="Approved">Distinct approved theme numbers of the round.</param>
public sealed record RoundTeamResult(Guid GroupId, int Approved, int Place, int Points);

/// <param name="Teams">Groups with at least one approved theme, best first.</param>
/// <param name="IsProvisional">The round has submissions that wait for a review: the result can still change.</param>
public sealed record RoundResult(ThemeRound Round, IReadOnlyList<RoundTeamResult> Teams, bool IsClosed, bool IsRunning, bool IsProvisional, int OpenSubmissions)
{
    public RoundTeamResult? For(Guid groupId) => Teams.FirstOrDefault(t => t.GroupId == groupId);

    /// <summary>"1. Team A (3), 2. Team B (2)", or a sentence when nobody scored.</summary>
    public string Ranking(Func<Guid, string> name) => Teams.Count == 0
        ? "geen goedgekeurde inzendingen."
        : string.Join(", ", Teams.Where(t => t.Points > 0).Select(t => $"{t.Place}. {name(t.GroupId)} ({t.Approved})"));
}

/// <param name="ApprovedLocations">Distinct approved location numbers, lowest first.</param>
/// <param name="UnlockedAssignments">Toeristen assignments unlocked: one per approved location.</param>
/// <param name="ThemePoints">Round points of closed rounds, capped.</param>
/// <param name="RunningRoundPoints">What the running round would give now; not part of the total yet.</param>
/// <param name="FinalPoints">The hand-entered final game points, capped.</param>
public sealed record AdventurersGroupScore(
    Guid GroupId,
    IReadOnlyList<string> ApprovedLocations,
    int UnlockedAssignments,
    int ThemePoints,
    int RunningRoundPoints,
    int FinalPoints,
    int Total);

public sealed record AdventurersStanding(IReadOnlyList<RoundResult> Rounds, IReadOnlyDictionary<Guid, AdventurersGroupScore> Groups)
{
    public static AdventurersStanding Empty { get; } = new(Array.Empty<RoundResult>(), new Dictionary<Guid, AdventurersGroupScore>());

    public AdventurersGroupScore? For(Guid groupId) => Groups.GetValueOrDefault(groupId);
}

/// <summary>
/// The Avonturiers rules, computed from the submissions every time and never stored, so a late review or a rebuild
/// from Slack always gives the right numbers. A theme counts for a round when its Slack message time falls inside the
/// round and its number belongs to the round; when it was reviewed does not matter.
/// </summary>
public static class AdventurersScoring
{
    public static AdventurersStanding Compute(
        IEnumerable<Guid> groupIds,
        IEnumerable<Submission> submissions,
        IReadOnlyList<ThemeRound> rounds,
        IReadOnlyDictionary<Guid, int> finalPoints,
        int themeMax,
        int finalMax,
        DateTimeOffset now)
    {
        var groups = groupIds.Distinct().ToList();
        var all = submissions.Where(s => s.Kind.IsAdventurers() && s.AssignmentCode != null).ToList();
        var results = rounds.OrderBy(r => r.Start).Select(r => RoundResultFor(r, all, now)).ToList();

        var scores = new Dictionary<Guid, AdventurersGroupScore>();
        foreach (var groupId in groups)
        {
            var locations = ApprovedLocations(all.Where(s => s.GroupId == groupId));
            var themePoints = results.Where(r => r.IsClosed).Sum(r => r.For(groupId)?.Points ?? 0);
            var running = results.Where(r => r.IsRunning).Sum(r => r.For(groupId)?.Points ?? 0);
            var capped = Math.Min(themePoints, Math.Max(0, themeMax));
            var final = Math.Clamp(finalPoints.GetValueOrDefault(groupId), 0, Math.Max(0, finalMax));
            scores[groupId] = new AdventurersGroupScore(groupId, locations, locations.Count, capped, running, final, capped + final);
        }

        return new AdventurersStanding(results, scores);
    }

    /// <summary>The distinct locations with an approved check-in: best status wins, each location counts once.</summary>
    public static IReadOnlyList<string> ApprovedLocations(IEnumerable<Submission> submissions) =>
        submissions
            .Where(s => s.Kind == SubmissionKind.CheckIn && s.AssignmentCode != null && IsApproved(s.Status))
            .Select(s => s.AssignmentCode!)
            .Distinct()
            .OrderBy(c => c, AssignmentCode.Comparer)
            .ToList();

    /// <summary>The round of one theme submission: its theme must belong to the round its message was posted in.</summary>
    public static ThemeRound? CountingRound(Submission submission, IReadOnlyList<ThemeRound> rounds) =>
        submission.Kind == SubmissionKind.Theme && submission.AssignmentCode is { } theme
            ? rounds.FirstOrDefault(r => r.Contains(submission.CreatedAt) && r.Themes.Contains(theme))
            : null;

    /// <summary>The round running at <paramref name="at"/>; null between rounds.</summary>
    public static ThemeRound? RoundAt(IReadOnlyList<ThemeRound> rounds, DateTimeOffset at) => rounds.FirstOrDefault(r => r.Contains(at));

    /// <summary>The round a theme number belongs to; null when it belongs to none.</summary>
    public static ThemeRound? RoundOfTheme(IReadOnlyList<ThemeRound> rounds, string theme) => rounds.FirstOrDefault(r => r.Themes.Contains(theme));

    public static RoundResult RoundResultFor(ThemeRound round, IReadOnlyList<Submission> submissions, DateTimeOffset now)
    {
        var inRound = submissions
            .Where(s => s.Kind == SubmissionKind.Theme && s.AssignmentCode != null && round.Contains(s.CreatedAt) && round.Themes.Contains(s.AssignmentCode))
            .ToList();

        var counts = inRound
            .Where(s => IsApproved(s.Status))
            .GroupBy(s => s.GroupId)
            .Select(g => (GroupId: g.Key, Approved: g.Select(s => s.AssignmentCode!).Distinct().Count()));
        var open = inRound.Count(s => s.Status is SubmissionStatus.Pending or SubmissionStatus.Received);
        return new RoundResult(round, Rank(counts), round.IsClosed(now), round.IsRunning(now), open > 0, open);
    }

    /// <summary>Most approved = 2 points, second = 1; equal counts share the place and the next place is skipped; 0 approved gets nothing.</summary>
    public static IReadOnlyList<RoundTeamResult> Rank(IEnumerable<(Guid GroupId, int Approved)> counts)
    {
        var scored = counts.Where(c => c.Approved > 0).ToList();
        return scored
            .Select(c =>
            {
                var place = 1 + scored.Count(o => o.Approved > c.Approved);
                return new RoundTeamResult(c.GroupId, c.Approved, place, place switch { 1 => 2, 2 => 1, _ => 0 });
            })
            .OrderBy(t => t.Place)
            .ThenByDescending(t => t.Approved)
            .ToList();
    }

    /// <summary>
    /// When the round points were earned, for the score chart: at the end of each closed round, capped so the
    /// line never passes <paramref name="themeMax"/>.
    /// </summary>
    public static IEnumerable<ScoreEvent> RoundEvents(AdventurersStanding standing, int themeMax, string label)
    {
        var total = new Dictionary<Guid, int>();
        foreach (var result in standing.Rounds.Where(r => r.IsClosed).OrderBy(r => r.Round.End))
        {
            foreach (var team in result.Teams.Where(t => t.Points > 0))
            {
                var before = total.GetValueOrDefault(team.GroupId);
                var after = Math.Min(before + team.Points, Math.Max(0, themeMax));
                total[team.GroupId] = after;
                if (after > before) yield return new ScoreEvent(team.GroupId, string.Empty, result.Round.End, after - before, label);
            }
        }
    }

    private static bool IsApproved(SubmissionStatus status) => status is SubmissionStatus.Approved or SubmissionStatus.Awesome;
}
