namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>
/// Fences the work of leader-only services. <see cref="LeaderOnlyHostedService{T}"/> starts a service inside a leader
/// term, which flows into everything that service runs. The moment this instance is no longer the leader, the term ends:
/// new Slack calls from that work are refused and calls in flight are cancelled, even while the service is still
/// stopping. Work outside a leader-only service (pages, the webhook) is not affected.
/// </summary>
public static class LeaderWork
{
    private static readonly AsyncLocal<Term?> Current = new();

    /// <summary>True inside a leader-only service on an instance that is no longer the leader.</summary>
    public static bool IsFenced => Current.Value is { Ended: true };

    /// <summary>Cancelled when the current leader term ends; never cancelled outside leader-only work.</summary>
    public static CancellationToken Token => Current.Value?.Token ?? CancellationToken.None;

    /// <summary>Starts a term for the current flow and what it starts from here.</summary>
    public static void Enter(IClusterLeadership leadership) => Current.Value = new Term(leadership);

    /// <exception cref="NotLeaderException">This is leader-only work and another instance is the leader now.</exception>
    public static void ThrowIfFenced()
    {
        if (IsFenced) throw new NotLeaderException();
    }

    private sealed class Term
    {
        private readonly IClusterLeadership _leadership;
        private readonly CancellationTokenSource _ended = new();

        public Term(IClusterLeadership leadership)
        {
            _leadership = leadership;
            _leadership.Changed += OnChanged;
            OnChanged();
        }

        public bool Ended => _ended.IsCancellationRequested || !_leadership.IsLeader;

        public CancellationToken Token => _ended.Token;

        private void OnChanged()
        {
            if (_leadership.IsLeader || _ended.IsCancellationRequested) return;
            _leadership.Changed -= OnChanged;
            _ended.Cancel();
        }
    }
}

/// <summary>Leader-only work was refused because another instance is the leader now.</summary>
public sealed class NotLeaderException() : InvalidOperationException("This instance is no longer the leader");
