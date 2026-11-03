using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Infrastructure.Cluster;

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
/// Signal that submission data changed, so open pages can reload.
/// Registered as a singleton; reaches the pages of the other instances of the app through <see cref="IClusterBus"/>.
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
    public const string ChangedTopic = "submissions";
    public const string CreatedTopic = "submission-created";
    public const string UnlockedTopic = "assignments-unlocked";

    private readonly ILogger<SubmissionChangeNotifier> _logger;
    private readonly IClusterBus _bus;

    public SubmissionChangeNotifier(ILogger<SubmissionChangeNotifier> logger, IClusterBus? bus = null)
    {
        _logger = logger;
        _bus = bus ?? SingleInstance.Instance;
        _bus.Subscribe(ChangedTopic, _ => RaiseChanged());
        _bus.Subscribe(CreatedTopic, payload =>
        {
            if (JsonSerializer.Deserialize<SubmissionCreatedNotification>(payload) is { } notification) RaiseCreated(notification);
        });
        _bus.Subscribe(UnlockedTopic, payload =>
        {
            if (JsonSerializer.Deserialize<AssignmentsUnlockedNotification>(payload) is { } notification) RaiseUnlocked(notification);
        });
        // Messages may have been missed while the connection was down
        _bus.Reconnected += RaiseChanged;
    }

    public event Action? Changed;

    public event Action<SubmissionCreatedNotification>? Created;

    public event Action<AssignmentsUnlockedNotification>? Unlocked;

    public void NotifyChanged()
    {
        RaiseChanged();
        _bus.Publish(ChangedTopic);
    }

    public void NotifyCreated(SubmissionCreatedNotification notification)
    {
        RaiseCreated(notification);
        _bus.Publish(CreatedTopic, JsonSerializer.Serialize(notification));
    }

    private void RaiseChanged()
    {
        foreach (var handler in Handlers(Changed).Cast<Action>())
        {
            Invoke(() => handler());
        }
    }

    private void RaiseCreated(SubmissionCreatedNotification notification)
    {
        foreach (var handler in Handlers(Created).Cast<Action<SubmissionCreatedNotification>>())
        {
            Invoke(() => handler(notification));
        }
    }

    public void NotifyUnlocked(AssignmentsUnlockedNotification notification)
    {
        RaiseUnlocked(notification);
        _bus.Publish(UnlockedTopic, JsonSerializer.Serialize(notification));
    }

    private void RaiseUnlocked(AssignmentsUnlockedNotification notification)
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
