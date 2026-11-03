using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>
/// Raised when an admin changes, blocks or removes an account, so open browser tabs check their login at once
/// instead of at their next periodic check. Reaches the tabs on the other instances of the app too.
/// </summary>
public sealed class AccountChangeNotifier
{
    public const string ClusterTopic = "account";

    private readonly IClusterBus _bus;

    public AccountChangeNotifier(IClusterBus? bus = null)
    {
        _bus = bus ?? SingleInstance.Instance;
        _bus.Subscribe(ClusterTopic, payload =>
        {
            if (Guid.TryParse(payload, out var userId)) Changed?.Invoke(userId);
        });
    }

    /// <summary>The account that changed; raised from any thread.</summary>
    public event Action<Guid>? Changed;

    public void Notify(Guid userId)
    {
        Changed?.Invoke(userId);
        _bus.Publish(ClusterTopic, userId.ToString());
    }
}
