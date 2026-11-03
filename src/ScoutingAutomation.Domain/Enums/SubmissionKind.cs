namespace ScoutingAutomation.Domain.Enums;

/// <summary>What a submission is for. Stored as a number; existing rows are <see cref="Assignment"/>.</summary>
public enum SubmissionKind
{
    /// <summary>A Toeristen assignment from the points file.</summary>
    Assignment = 0,

    /// <summary>An Avonturiers photo at a location: it unlocks a Toeristen assignment.</summary>
    CheckIn = 1,

    /// <summary>An Avonturiers photo for a theme round.</summary>
    Theme = 2
}

public static class SubmissionKindExtensions
{
    /// <summary>The Dutch name shown in the app.</summary>
    public static string KindLabel(this SubmissionKind kind) => kind switch
    {
        SubmissionKind.CheckIn => "Check-in",
        SubmissionKind.Theme => "Half-uur opdracht",
        _ => "Toeristen"
    };

    /// <summary>How the number is written: "12" for an assignment, "L7" for a location, "T3" for a half-hour assignment (theme).</summary>
    public static string Label(this SubmissionKind kind, string? code) => code == null ? "?" : kind switch
    {
        SubmissionKind.CheckIn => $"L{code}",
        SubmissionKind.Theme => $"T{code}",
        _ => code
    };

    /// <summary>Whether this kind belongs to the Avonturiers.</summary>
    public static bool IsAdventurers(this SubmissionKind kind) => kind != SubmissionKind.Assignment;

    /// <summary>
    /// The status a review gives a submission of this kind: Avonturiers have no awesome tier, so awesome counts as approved.
    /// </summary>
    public static SubmissionStatus Effective(this SubmissionKind kind, SubmissionStatus status) =>
        kind.IsAdventurers() && status == SubmissionStatus.Awesome ? SubmissionStatus.Approved : status;
}

/// <summary>Which of a group's two channels a Slack message came from.</summary>
public enum ChannelRole
{
    Toeristen,
    Avonturiers
}
