using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Workflow;

/// <summary>New open submissions from one Slack message, for the live notifications in the browser.</summary>
public sealed record SubmissionCreatedNotification(
    Guid GroupId,
    string GroupName,
    string? GroupAbbreviation,
    string AssignmentCode,
    string SubmitterName,
    IReadOnlyList<Guid> SubmissionIds,
    DateTimeOffset PostedAt);

/// <summary>The Avonturiers of a group opened new Toeristen assignments, for the live notification on the group's pages.</summary>
/// <param name="NewCodes">The assignments that just opened, lowest first.</param>
/// <param name="OpenCount">Numbered assignments open now.</param>
/// <param name="Total">All numbered assignments.</param>
public sealed record AssignmentsUnlockedNotification(Guid GroupId, IReadOnlyList<string> NewCodes, int OpenCount, int Total);

/// <summary>
/// In-process signal that submission data changed, so open pages can reload.
/// Registered as a singleton; only reaches pages served by this app instance.
/// </summary>
public interface ISubmissionChangeNotifier
{
    event Action? Changed;

    /// <summary>Raised for new submissions from live Slack messages; not for a rebuild from history.</summary>
    event Action<SubmissionCreatedNotification>? Created;

    /// <summary>Raised when a group's Avonturiers opened new assignments live; not for a catch-up or rebuild.</summary>
    event Action<AssignmentsUnlockedNotification>? Unlocked;

    void NotifyChanged();
    void NotifyCreated(SubmissionCreatedNotification notification);
    void NotifyUnlocked(AssignmentsUnlockedNotification notification);
}

public class SubmissionChangeNotifier : ISubmissionChangeNotifier
{
    private readonly ILogger<SubmissionChangeNotifier> _logger;

    public SubmissionChangeNotifier(ILogger<SubmissionChangeNotifier> logger)
    {
        _logger = logger;
    }

    public event Action? Changed;

    public event Action<SubmissionCreatedNotification>? Created;

    public event Action<AssignmentsUnlockedNotification>? Unlocked;

    public void NotifyChanged()
    {
        foreach (var handler in Handlers(Changed).Cast<Action>())
        {
            Invoke(() => handler());
        }
    }

    public void NotifyCreated(SubmissionCreatedNotification notification)
    {
        foreach (var handler in Handlers(Created).Cast<Action<SubmissionCreatedNotification>>())
        {
            Invoke(() => handler(notification));
        }
    }

    public void NotifyUnlocked(AssignmentsUnlockedNotification notification)
    {
        foreach (var handler in Handlers(Unlocked).Cast<Action<AssignmentsUnlockedNotification>>())
        {
            Invoke(() => handler(notification));
        }
    }

    private static Delegate[] Handlers(Delegate? handlers) => handlers?.GetInvocationList() ?? Array.Empty<Delegate>();

    private void Invoke(Action handler)
    {
        // One failing page must not stop the others or the caller
        try
        {
            handler();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Submission change handler failed");
        }
    }
}
