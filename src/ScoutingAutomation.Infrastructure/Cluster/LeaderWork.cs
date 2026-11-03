namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>
/// Fences the work of leader-only services. <see cref="LeaderOnlyHostedService{T}"/> starts a service inside a leader
/// context, which flows into everything that service runs. The moment this instance is no longer the leader, that work
/// is refused at the Slack boundary, even while the service is still stopping: an old leader cannot post or take events
/// after it noticed another instance took over. Work outside a leader-only service (pages, the webhook) is not affected.
/// </summary>
public static class LeaderWork
{
    private static readonly AsyncLocal<IClusterLeadership?> Leadership = new();

    /// <summary>True inside a leader-only service on an instance that is no longer the leader.</summary>
    public static bool IsFenced => Leadership.Value is { IsLeader: false };

    /// <summary>Marks the current flow, and what it starts from here, as leader-only work.</summary>
    public static void Enter(IClusterLeadership leadership) => Leadership.Value = leadership;

    /// <exception cref="NotLeaderException">This is leader-only work and another instance is the leader now.</exception>
    public static void ThrowIfFenced()
    {
        if (IsFenced) throw new NotLeaderException();
    }
}

/// <summary>Leader-only work was refused because another instance is the leader now.</summary>
public sealed class NotLeaderException() : InvalidOperationException("This instance is no longer the leader");
