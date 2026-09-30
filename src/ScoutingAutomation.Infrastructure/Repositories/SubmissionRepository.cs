using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Repositories;

public class SubmissionRepository : ISubmissionRepository
{
    private readonly ScoutingDbContext _context;

    public SubmissionRepository(ScoutingDbContext context)
    {
        _context = context;
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

        if (filter.AssignmentNumber.HasValue)
        {
            query = query.Where(s => s.AssignmentNumber == filter.AssignmentNumber.Value);
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

    private sealed record AssignmentOutcome(Guid GroupId, int AssignmentNumber, SubmissionStatus Status);

    /// <summary>
    /// One outcome per group and assignment, using the best status of all its submissions.
    /// </summary>
    private static List<AssignmentOutcome> GetAssignmentOutcomes(IEnumerable<Submission> submissions)
    {
        return submissions
            .Where(s => s.AssignmentNumber.HasValue)
            .GroupBy(s => (s.GroupId, Number: s.AssignmentNumber!.Value))
            .Select(g => new AssignmentOutcome(
                g.Key.GroupId,
                g.Key.Number,
                g.Select(s => s.Status).Best()))
            .ToList();
    }

    private static int CountStatus(IEnumerable<AssignmentOutcome> outcomes, SubmissionStatus status) =>
        outcomes.Count(o => o.Status.Rank() == status.Rank());

    private static int CalculateScore(IEnumerable<AssignmentOutcome> groupOutcomes, IReadOnlyDictionary<int, Assignment> assignmentsByNumber)
    {
        return groupOutcomes.Sum(o =>
            assignmentsByNumber.TryGetValue(o.AssignmentNumber, out var assignment) ? assignment.PointsFor(o.Status) : 0);
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
            UnresolvedCount = submissions.Count(s => !s.AssignmentNumber.HasValue)
        };

        // Group Stats
        var assignmentsByNumber = assignments.ToDictionary(a => a.Number);
        var groups = await _context.Groups.AsNoTracking().ToListAsync(ct);
        foreach (var group in groups)
        {
            var groupOutcomes = outcomes.Where(o => o.GroupId == group.Id).ToList();
            stats.GroupStatistics.Add(new GroupStatistic
            {
                GroupId = group.Id,
                GroupName = group.Name,
                Score = CalculateScore(groupOutcomes, assignmentsByNumber),
                TotalSubmissions = groupOutcomes.Count,
                PendingCount = CountStatus(groupOutcomes, SubmissionStatus.Pending),
                ApprovedCount = CountStatus(groupOutcomes, SubmissionStatus.Approved),
                RejectedCount = CountStatus(groupOutcomes, SubmissionStatus.Rejected),
                AwesomeCount = CountStatus(groupOutcomes, SubmissionStatus.Awesome)
            });
        }

        foreach (var groupStat in stats.GroupStatistics)
        {
            groupStat.Rank = 1 + stats.GroupStatistics.Count(other => other.Score > groupStat.Score);
        }

        // Assignment Stats
        var assignmentNumbers = assignments.Select(a => a.Number)
            .Union(outcomes.Select(o => o.AssignmentNumber))
            .Distinct()
            .OrderBy(n => n);

        foreach (var num in assignmentNumbers)
        {
            var assignOutcomes = outcomes.Where(o => o.AssignmentNumber == num).ToList();

            stats.AssignmentStatistics.Add(new AssignmentStatistic
            {
                AssignmentNumber = num,
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
        await _context.SaveChangesAsync(ct);
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

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        _context.Reviews.RemoveRange(await _context.Reviews.ToListAsync(ct));
        _context.Submissions.RemoveRange(await _context.Submissions.ToListAsync(ct));
        await _context.SaveChangesAsync(ct);
    }

    public async Task<bool> ExistsAsync(string channelId, string messageId, string fileName, CancellationToken ct = default)
    {
        return await _context.Submissions
            .AnyAsync(s => s.SlackChannelId == channelId && s.SlackMessageId == messageId && s.FileName == fileName, ct);
    }
}
