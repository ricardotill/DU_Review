using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Domain.Models;

/// <summary>
/// Counts are per group and assignment: resubmissions are merged and only the best status counts.
/// </summary>
public class SubmissionStatistics
{
    public int TotalSubmissions { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int AwesomeCount { get; set; }

    /// <summary>
    /// Uploads without a (valid) assignment number. These are not part of the other counts.
    /// </summary>
    public int UnresolvedCount { get; set; }

    public double OverallApprovalRate => TotalSubmissions > 0
        ? Math.Round(((double)(ApprovedCount + AwesomeCount) / TotalSubmissions) * 100, 1)
        : 0;

    public List<GroupStatistic> GroupStatistics { get; set; } = new();
    public List<AssignmentStatistic> AssignmentStatistics { get; set; } = new();
    public List<DailyActivityStatistic> RecentActivity { get; set; } = new();
}

public class GroupStatistic
{
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = string.Empty;
    public int Score { get; set; }

    /// <summary>Leaderboard position by score; equal scores share a rank (1, 1, 3).</summary>
    public int Rank { get; set; }
    public int TotalSubmissions { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int AwesomeCount { get; set; }

    public double ApprovalRate => TotalSubmissions > 0
        ? Math.Round(((double)(ApprovedCount + AwesomeCount) / TotalSubmissions) * 100, 1)
        : 0;
}

public class AssignmentStatistic
{
    public int AssignmentNumber { get; set; }
    public int TotalSubmissions { get; set; }
    public int ApprovedCount { get; set; }
    public int PendingCount { get; set; }
    public int AwesomeCount { get; set; }
    public int RejectedCount { get; set; }
}

public class DailyActivityStatistic
{
    public DateOnly Date { get; set; }
    public int Count { get; set; }
}
