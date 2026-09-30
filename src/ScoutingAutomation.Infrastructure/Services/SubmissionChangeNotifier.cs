using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// In-process signal that submission data changed, so open pages can reload.
/// Registered as a singleton; only reaches pages served by this app instance.
/// </summary>
public interface ISubmissionChangeNotifier
{
    event Action? Changed;
    void NotifyChanged();
}

public class SubmissionChangeNotifier : ISubmissionChangeNotifier
{
    private readonly ILogger<SubmissionChangeNotifier> _logger;

    public SubmissionChangeNotifier(ILogger<SubmissionChangeNotifier> logger)
    {
        _logger = logger;
    }

    public event Action? Changed;

    public void NotifyChanged()
    {
        var handlers = Changed?.GetInvocationList() ?? Array.Empty<Delegate>();
        foreach (var handler in handlers.Cast<Action>())
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
}
