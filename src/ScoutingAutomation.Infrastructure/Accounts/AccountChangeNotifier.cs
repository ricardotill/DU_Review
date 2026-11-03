namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>
/// Raised when an admin changes, blocks or removes an account, so open browser tabs check their login at once
/// instead of at their next periodic check.
/// </summary>
public sealed class AccountChangeNotifier
{
    /// <summary>The account that changed; raised from any thread.</summary>
    public event Action<Guid>? Changed;

    public void Notify(Guid userId) => Changed?.Invoke(userId);
}
