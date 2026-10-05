using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Where a group stands at one location, as the map colours it.</summary>
public enum LocationStatus
{
    /// <summary>Nothing posted yet: grey.</summary>
    Open,

    /// <summary>Posted and waiting for a review: orange.</summary>
    Waiting,

    /// <summary>Approved: green.</summary>
    Approved,

    /// <summary>Rejected, and nothing newer posted: red. The group can post again.</summary>
    Rejected
}

/// <param name="Submission">The check-in that decides the status: the approved one, else the newest.</param>
public sealed record LocationState(LocationStatus Status, DateTimeOffset? LastPostAt, Submission? Submission);

/// <summary>The status of the locations per group, from the check-ins. Best status wins, as for the scores.</summary>
public static class AdventurersMap
{
    public static LocationState StateOf(IEnumerable<Submission> submissions, Guid groupId, int number)
    {
        var code = number.ToString();
        var checkIns = submissions
            .Where(s => s.Kind == SubmissionKind.CheckIn && s.GroupId == groupId && s.AssignmentCode == code)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        if (checkIns.Count == 0) return new LocationState(LocationStatus.Open, null, null);

        var last = checkIns[0].CreatedAt;
        var approved = checkIns.FirstOrDefault(s => s.Kind.Effective(s.Status) == SubmissionStatus.Approved);
        if (approved != null) return new LocationState(LocationStatus.Approved, last, approved);

        var waiting = checkIns.FirstOrDefault(s => s.Status is SubmissionStatus.Pending or SubmissionStatus.Received);
        if (waiting != null) return new LocationState(LocationStatus.Waiting, last, waiting);

        return new LocationState(LocationStatus.Rejected, last, checkIns[0]);
    }

    /// <summary>The marker colour on the map.</summary>
    public static string Color(this LocationStatus status) => status switch
    {
        LocationStatus.Waiting => "#f59e0b",
        LocationStatus.Approved => "#10b981",
        LocationStatus.Rejected => "#ef4444",
        _ => "#94a3b8"
    };

    /// <summary>The Dutch name shown next to the marker.</summary>
    public static string Label(this LocationStatus status) => status switch
    {
        LocationStatus.Waiting => "Wacht op beoordeling",
        LocationStatus.Approved => "Goedgekeurd",
        LocationStatus.Rejected => "Afgekeurd: probeer opnieuw",
        _ => "Nog niet bezocht"
    };
}
