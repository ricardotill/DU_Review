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

/// <summary>
/// In-process signal that submission data changed, so open pages can reload.
/// Registered as a singleton; only reaches pages served by this app instance.
/// </summary>
public interface ISubmissionChangeNotifier
{
    event Action? Changed;

    /// <summary>Raised for new submissions from live Slack messages; not for a rebuild from history.</summary>
    event Action<SubmissionCreatedNotification>? Created;

    void NotifyChanged();
    void NotifyCreated(SubmissionCreatedNotification notification);
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
