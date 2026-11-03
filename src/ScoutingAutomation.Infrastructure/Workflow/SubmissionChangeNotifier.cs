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

/// <summary>
/// Signal that submission data changed, so open pages can reload.
/// Registered as a singleton; reaches the pages of the other instances of the app through <see cref="IClusterBus"/>.
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
    public const string ChangedTopic = "submissions";
    public const string CreatedTopic = "submission-created";

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
        // Messages may have been missed while the connection was down
        _bus.Reconnected += RaiseChanged;
    }

    public event Action? Changed;

    public event Action<SubmissionCreatedNotification>? Created;

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
