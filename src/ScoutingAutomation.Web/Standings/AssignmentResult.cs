using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Models;

namespace ScoutingAutomation.Web.Standings;

/// <summary>How one group did on one assignment: resubmissions merged, the best status counts.</summary>
/// <param name="Code">The assignment's code: a number, or letters for a special assignment.</param>
/// <param name="Title">The assignment's title from the points file; null when it has none.</param>
/// <param name="Uploads">Newest first.</param>
/// <param name="CountingSubmissionId">The newest submission with the best status: the one that counts.</param>
public sealed record AssignmentResult(string Code, string? Title, SubmissionStatus BestStatus, int Points, List<Submission> Uploads, Guid CountingSubmissionId)
{
    /// <summary>One result per assignment among <paramref name="submissions"/> of one group, numbers first, then letters.</summary>
    public static List<AssignmentResult> For(IEnumerable<Submission> submissions, IReadOnlyDictionary<string, Assignment> assignments) =>
        submissions
            .Where(s => s.AssignmentCode != null)
            .GroupBy(s => s.AssignmentCode!)
            .Select(g =>
            {
                var uploads = g.OrderByDescending(s => s.CreatedAt).ToList();
                var best = uploads.Select(s => s.Status).Best();
                var assignment = assignments.GetValueOrDefault(g.Key);
                var points = assignment?.PointsFor(best) ?? 0;
                return new AssignmentResult(g.Key, assignment?.Title, best, points, uploads, uploads.First(s => s.Status == best).Id);
            })
            .OrderBy(r => r.Code, AssignmentCode.Comparer)
            .ToList();
}
