using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class SubmissionRepository : ISubmissionRepository
{
    private readonly ScoutingDbContext _context;
    private readonly RuntimeSettingsService? _runtime;

    /// <param name="runtime">Gives the bonus categories in use; without it the default categories count.</param>
    public SubmissionRepository(ScoutingDbContext context, RuntimeSettingsService? runtime = null)
    {
        _context = context;
        _runtime = runtime;
    }

    public async Task<Submission?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var submission = await _context.Submissions
            .Include(s => s.Group)
            .Include(s => s.Reviews)
            .FirstOrDefaultAsync(s => s.Id == id, ct);

        if (submission != null)
        {
            submission.Reviews = submission.Reviews.OrderByDescending(r => r.CreatedAt).ToList();
        }

        return submission;
    }

    public async Task<IReadOnlyList<Submission>> GetBySlackMessageIdAsync(string channelId, string messageId, CancellationToken ct = default)
    {
        var submissions = await _context.Submissions
            .Include(s => s.Group)
            .Include(s => s.Reviews)
            .Where(s => s.SlackChannelId == channelId && s.SlackMessageId == messageId)
            .ToListAsync(ct);

        foreach (var submission in submissions)
        {
            submission.Reviews = submission.Reviews.OrderByDescending(r => r.CreatedAt).ToList();
        }

        return submissions.OrderBy(s => s.FileName).ToList();
    }

    public async Task<IReadOnlyList<Submission>> SearchSubmissionsAsync(SubmissionFilter filter, CancellationToken ct = default)
    {
        var query = _context.Submissions
            .Include(s => s.Group)
            .Include(s => s.Reviews)
            .AsNoTracking()
            .AsQueryable();

        if (filter.GroupId.HasValue)
        {
            query = query.Where(s => s.GroupId == filter.GroupId.Value);
        }

        if (filter.AssignmentCode != null)
        {
            query = query.Where(s => s.AssignmentCode == filter.AssignmentCode);
        }

        if (filter.Status.HasValue)
        {
            query = query.Where(s => s.Status == filter.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Reviewer))
        {
            var reviewer = filter.Reviewer.Trim().ToLower();
            var ids = filter.ReviewerIds.ToList();
            query = query.Where(s => s.Reviews.Any(r => r.Reviewer.ToLower().Contains(reviewer) || ids.Contains(r.Reviewer)));
        }

        if (filter.FromDate.HasValue)
        {
            query = query.Where(s => s.CreatedAt >= filter.FromDate.Value);
        }

        if (filter.ToDate.HasValue)
        {
            query = query.Where(s => s.CreatedAt <= filter.ToDate.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var term = filter.SearchTerm.Trim().ToLower();
            query = query.Where(s =>
                s.FileName.ToLower().Contains(term) ||
                s.Submitter.ToLower().Contains(term) ||
                (s.SubmitterName != null && s.SubmitterName.ToLower().Contains(term)) ||
                (s.MessageText != null && s.MessageText.ToLower().Contains(term)) ||
                (s.Group != null && s.Group.Name.ToLower().Contains(term)));
        }

        var list = await query.ToListAsync(ct);
        return list.OrderByDescending(s => s.CreatedAt).ToList();
    }

    /// <param name="AchievedAt">Post time of the first submission with the best status.</param>
    private sealed record AssignmentOutcome(Guid GroupId, string AssignmentCode, SubmissionStatus Status, DateTimeOffset AchievedAt);

    /// <summary>
    /// One outcome per group and assignment, using the best status of all its submissions.
    /// </summary>
    private static List<AssignmentOutcome> GetAssignmentOutcomes(IEnumerable<Submission> submissions)
    {
        return submissions
            .Where(s => s.AssignmentCode != null)
            .GroupBy(s => (s.GroupId, Code: s.AssignmentCode!))
            .Select(g =>
            {
                var best = g.Select(s => s.Status).Best();
                return new AssignmentOutcome(
                    g.Key.GroupId,
                    g.Key.Code,
                    best,
                    g.Where(s => s.Status.Rank() == best.Rank()).Min(s => s.CreatedAt));
            })
            .ToList();
    }

    private static int CountStatus(IEnumerable<AssignmentOutcome> outcomes, SubmissionStatus status) =>
        outcomes.Count(o => o.Status.Rank() == status.Rank());

    private static int CalculateScore(IEnumerable<AssignmentOutcome> groupOutcomes, IReadOnlyDictionary<string, Assignment> assignmentsByCode)
    {
        return groupOutcomes.Sum(o =>
            assignmentsByCode.TryGetValue(o.AssignmentCode, out var assignment) ? assignment.PointsFor(o.Status) : 0);
    }

    /// <summary>Points entered by hand, on the chart at each change; only categories in use.</summary>
    private static IEnumerable<ScoreEvent> BonusEvents(IEnumerable<Group> groups, IReadOnlyList<BonusCategoryConfig> categories,
        IReadOnlyDictionary<(Guid, string), GroupBonusPoints> current, IReadOnlyCollection<BonusPointsEntry> history)
    {
        foreach (var group in groups)
        {
            foreach (var category in categories)
            {
                var row = current.GetValueOrDefault((group.Id, category.Key));
                foreach (var e in CategoryEvents(group, category, row?.Points ?? 0, row?.UpdatedAt, history))
                {
                    yield return e;
                }
            }
        }
    }

    /// <summary>
    /// Changes this close together are one step on the chart: a typo fixed a minute later (100, then 10)
    /// must not draw a spike that squashes every other line on the beamer.
    /// </summary>
    public static readonly TimeSpan BonusMergeWindow = TimeSpan.FromMinutes(5);

    private static IEnumerable<ScoreEvent> CategoryEvents(
        Group group, BonusCategoryConfig category, int current, DateTimeOffset? updatedAt, IReadOnlyCollection<BonusPointsEntry> history)
    {
        var steps = history
            .Where(h => h.GroupId == group.Id && h.CategoryKey == category.Key)
            .OrderBy(h => h.ChangedAt)
            .Select(h => (At: h.ChangedAt, Points: h.Delta))
            .ToList();

        // The current value on the group is what the scoreboard shows, so the chart follows it whatever the history says.
        // Points from before the history was kept go before the first change; a surplus in the history (which should not
        // happen) is taken off at the last change, so the line never dips below zero on the way.
        var missing = current - steps.Sum(h => h.Points);
        if (missing > 0)
        {
            steps.Insert(0, (steps.Count > 0 ? steps[0].At : updatedAt ?? group.CreatedAt, missing));
        }
        else if (missing < 0)
        {
            steps.Add((steps[^1].At, missing));
        }

        var label = category.Name;
        DateTimeOffset? bucketAt = null;
        var bucketPoints = 0;
        foreach (var step in steps)
        {
            if (bucketAt != null && step.At - bucketAt.Value >= BonusMergeWindow)
            {
                if (bucketPoints != 0) yield return new ScoreEvent(group.Id, string.Empty, bucketAt.Value, bucketPoints, label);
                bucketAt = null;
                bucketPoints = 0;
            }

            bucketAt ??= step.At;
            bucketPoints += step.Points;
        }

        if (bucketAt != null && bucketPoints != 0)
        {
            yield return new ScoreEvent(group.Id, string.Empty, bucketAt.Value, bucketPoints, label);
        }
    }

    public async Task<SubmissionStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var submissions = await _context.Submissions
            .Include(s => s.Group)
            .AsNoTracking()
            .ToListAsync(ct);

        var assignments = await _context.Assignments
            .AsNoTracking()
            .ToListAsync(ct);

        // Metrics count group/assignment outcomes, not individual uploads
        var outcomes = GetAssignmentOutcomes(submissions);

        var stats = new SubmissionStatistics
        {
            TotalSubmissions = outcomes.Count,
            PendingCount = CountStatus(outcomes, SubmissionStatus.Pending),
            ApprovedCount = CountStatus(outcomes, SubmissionStatus.Approved),
            RejectedCount = CountStatus(outcomes, SubmissionStatus.Rejected),
            AwesomeCount = CountStatus(outcomes, SubmissionStatus.Awesome),
            UnresolvedCount = submissions.Count(s => s.AssignmentCode == null)
        };

        // Group Stats
        var assignmentsByCode = assignments.ToDictionary(a => a.Code);
        var groups = await _context.Groups.AsNoTracking().ToListAsync(ct);
        var bonusHistory = await _context.BonusPointsEntries.AsNoTracking().ToListAsync(ct);

        // Points of a category that is no longer in use are kept, but count nowhere
        var categories = _runtime?.BonusCategories ?? BonusCategoryConfig.Defaults;
        var bonusPoints = (await _context.GroupBonusPoints.AsNoTracking().ToListAsync(ct))
            .ToDictionary(p => (p.GroupId, p.CategoryKey));
        foreach (var group in groups)
        {
            var groupOutcomes = outcomes.Where(o => o.GroupId == group.Id).ToList();
            var groupBonus = categories.ToDictionary(c => c.Key, c => bonusPoints.GetValueOrDefault((group.Id, c.Key))?.Points ?? 0);
            stats.GroupStatistics.Add(new GroupStatistic
            {
                GroupId = group.Id,
                GroupName = group.Name,
                Score = CalculateScore(groupOutcomes, assignmentsByCode) + groupBonus.Values.Sum(),
                BonusPoints = groupBonus,
                TotalSubmissions = groupOutcomes.Count,
                PendingCount = CountStatus(groupOutcomes, SubmissionStatus.Pending),
                ApprovedCount = CountStatus(groupOutcomes, SubmissionStatus.Approved),
                RejectedCount = CountStatus(groupOutcomes, SubmissionStatus.Rejected),
                AwesomeCount = CountStatus(groupOutcomes, SubmissionStatus.Awesome),
                LastSubmissionAt = submissions.Where(s => s.GroupId == group.Id).Select(s => (DateTimeOffset?)s.CreatedAt).Max()
            });
        }

        stats.ScoreEvents = outcomes
            .Select(o => new ScoreEvent(
                o.GroupId,
                o.AssignmentCode,
                o.AchievedAt,
                assignmentsByCode.TryGetValue(o.AssignmentCode, out var assignment) ? assignment.PointsFor(o.Status) : 0))
            .Where(e => e.Points > 0)
            .Concat(BonusEvents(groups, categories, bonusPoints, bonusHistory))
            .OrderBy(e => e.At)
            .ToList();

        foreach (var groupStat in stats.GroupStatistics)
        {
            groupStat.Rank = 1 + stats.GroupStatistics.Count(other => other.Score > groupStat.Score);
        }

        // Assignment Stats
        var assignmentCodes = assignments.Select(a => a.Code)
            .Union(outcomes.Select(o => o.AssignmentCode))
            .Distinct()
            .OrderBy(c => c, AssignmentCode.Comparer);

        foreach (var code in assignmentCodes)
        {
            var assignOutcomes = outcomes.Where(o => o.AssignmentCode == code).ToList();

            stats.AssignmentStatistics.Add(new AssignmentStatistic
            {
                AssignmentCode = code,
                TotalSubmissions = assignOutcomes.Count,
                ApprovedCount = CountStatus(assignOutcomes, SubmissionStatus.Approved),
                PendingCount = CountStatus(assignOutcomes, SubmissionStatus.Pending),
                AwesomeCount = CountStatus(assignOutcomes, SubmissionStatus.Awesome),
                RejectedCount = CountStatus(assignOutcomes, SubmissionStatus.Rejected)
            });
        }

        // Activity Stats (last 7 days)
        var sevenDaysAgo = DateTimeOffset.UtcNow.Date.AddDays(-6);
        for (var i = 0; i < 7; i++)
        {
            var day = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-6 + i));
            var count = submissions.Count(s => DateOnly.FromDateTime(s.CreatedAt.Date) == day);
            stats.RecentActivity.Add(new DailyActivityStatistic
            {
                Date = day,
                Count = count
            });
        }

        return stats;
    }

    public async Task AddAsync(Submission submission, CancellationToken ct = default)
    {
        await _context.Submissions.AddAsync(submission, ct);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (DbErrors.IsUniqueViolation(ex))
        {
            // Not saved: stop tracking it, or the next save would try again
            _context.Entry(submission).State = EntityState.Detached;
            throw new DuplicateSubmissionException(
                $"File {submission.SlackFileId} of message {submission.SlackMessageId} is already registered", ex);
        }
    }

    public async Task UpdateAsync(Submission submission, CancellationToken ct = default)
    {
        var entry = _context.Entry(submission);
        if (entry.State == EntityState.Detached)
        {
            _context.Submissions.Attach(submission);
            entry.State = EntityState.Modified;
        }

        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(IReadOnlyList<Submission> submissions, CancellationToken ct = default)
    {
        var ids = submissions.Select(s => s.Id).ToList();
        _context.Reviews.RemoveRange(await _context.Reviews.Where(r => ids.Contains(r.SubmissionId)).ToListAsync(ct));
        _context.Submissions.RemoveRange(await _context.Submissions.Where(s => ids.Contains(s.Id)).ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        _context.Reviews.RemoveRange(await _context.Reviews.ToListAsync(ct));
        _context.Submissions.RemoveRange(await _context.Submissions.ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    public async Task DeleteByGroupAsync(Guid groupId, CancellationToken ct = default)
    {
        _context.Reviews.RemoveRange(await _context.Reviews.Where(r => r.Submission!.GroupId == groupId).ToListAsync(ct));
        _context.Submissions.RemoveRange(await _context.Submissions.Where(s => s.GroupId == groupId).ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> ExistsAsync(string channelId, string messageId, string? fileId, string fileName, CancellationToken ct = default)
    {
        var fromMessage = _context.Submissions.Where(s => s.SlackChannelId == channelId && s.SlackMessageId == messageId);

        // Submissions registered before file IDs were stored only have a name
        return string.IsNullOrEmpty(fileId)
            ? await fromMessage.AnyAsync(s => s.FileName == fileName, ct)
            : await fromMessage.AnyAsync(s => s.SlackFileId == fileId || (s.SlackFileId == null && s.FileName == fileName), ct);
    }
}
