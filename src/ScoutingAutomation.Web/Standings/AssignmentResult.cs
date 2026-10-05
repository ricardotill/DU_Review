using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Web.Standings;

/// <summary>How one group did on one assignment: resubmissions merged, the best status counts.</summary>
/// <param name="Uploads">Newest first.</param>
/// <param name="CountingSubmissionId">The newest submission with the best status: the one that counts.</param>
public sealed record AssignmentResult(int Number, SubmissionStatus BestStatus, int Points, List<Submission> Uploads, Guid CountingSubmissionId)
{
    /// <summary>One result per assignment number among <paramref name="submissions"/> of one group, by number.</summary>
    public static List<AssignmentResult> For(IEnumerable<Submission> submissions, IReadOnlyDictionary<int, Assignment> assignments) =>
        submissions
            .Where(s => s.AssignmentNumber.HasValue)
            .GroupBy(s => s.AssignmentNumber!.Value)
            .Select(g =>
            {
                var uploads = g.OrderByDescending(s => s.CreatedAt).ToList();
                var best = uploads.Select(s => s.Status).Best();
                var points = assignments.TryGetValue(g.Key, out var assignment) ? assignment.PointsFor(best) : 0;
                return new AssignmentResult(g.Key, best, points, uploads, uploads.First(s => s.Status == best).Id);
            })
            .OrderBy(r => r.Number)
            .ToList();
}
