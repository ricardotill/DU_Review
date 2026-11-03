namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>
/// Locks shared by every instance of the app on the same database. With one instance (SQLite) they are no-ops:
/// the callers' own in-process locks already do the work. With PostgreSQL they are advisory locks, so two instances
/// take turns the same way two threads of one instance do.
/// </summary>
public interface IClusterLocks
{
    /// <summary>Whether the locks reach other instances.</summary>
    bool IsDistributed { get; }

    /// <summary>Waits until no instance holds <paramref name="name"/>, then holds it until disposed.</summary>
    Task<ClusterLock> AcquireAsync(string name, CancellationToken ct = default);

    /// <summary>Waits until no instance holds <paramref name="name"/> exclusively; others may share it meanwhile.</summary>
    Task<ClusterLock> AcquireSharedAsync(string name, CancellationToken ct = default);
}

/// <summary>A held cluster lock; disposing gives it back.</summary>
public abstract class ClusterLock : IDisposable, IAsyncDisposable
{
    public static readonly ClusterLock None = new NoLock();

    public abstract void Dispose();

    public virtual ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private sealed class NoLock : ClusterLock
    {
        public override void Dispose()
        {
        }
    }
}

/// <summary>
/// Which instance does the work that must happen once: the Slack connection, the catch-up at start, and the scheduled
/// Slack messages. Every instance serves the pages.
/// </summary>
public interface IClusterLeadership
{
    bool IsLeader { get; }

    /// <summary>Raised when this instance becomes or stops being the leader; from any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Small messages between the instances, so a change made on one shows on the pages of all of them.
/// With one instance nothing is sent: the callers raise their own events locally.
/// </summary>
public interface IClusterBus
{
    /// <summary>Sends <paramref name="payload"/> to the other instances; it never reaches this instance's own handlers.</summary>
    void Publish(string topic, string payload = "");

    /// <summary>Runs <paramref name="handler"/> for each message on <paramref name="topic"/> from another instance.</summary>
    void Subscribe(string topic, Action<string> handler);

    /// <summary>
    /// Raised after the connection to the other instances was lost and is back: messages may have been missed, so whoever
    /// keeps a copy of shared data reloads it.
    /// </summary>
    event Action? Reconnected;
}

/// <summary>The cluster parts for a single instance: no-op locks, always the leader, a bus that sends nothing.</summary>
public sealed class SingleInstance : IClusterLocks, IClusterLeadership, IClusterBus
{
    public static readonly SingleInstance Instance = new();

    public bool IsDistributed => false;

    public Task<ClusterLock> AcquireAsync(string name, CancellationToken ct = default) => Task.FromResult(ClusterLock.None);

    public Task<ClusterLock> AcquireSharedAsync(string name, CancellationToken ct = default) => Task.FromResult(ClusterLock.None);

    public bool IsLeader => true;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public void Publish(string topic, string payload = "")
    {
    }

    public void Subscribe(string topic, Action<string> handler)
    {
    }

    public event Action? Reconnected
    {
        add { }
        remove { }
    }
}

/// <summary>Names of the cluster locks, in one place so two parts of the app never pick the same name by accident.</summary>
public static class ClusterLockNames
{
    /// <summary>Live Slack processing takes it shared; a rebuild or an edition switch takes it alone.</summary>
    public const string SlackGate = "slack-gate";

    public static string Channel(string? channel) => "slack-channel:" + (channel ?? "");

    public static string Unlock(Guid groupId) => "unlock:" + groupId;

    public const string Editions = "editions";

    public const string Accounts = "accounts";

    public const string SlackRetry = "slack-retry";

    public const string DatabaseSetup = "database-setup";

    public const string Leader = "leader";
}

public static class ClusterLocksExtensions
{
    /// <summary>
    /// Runs <paramref name="work"/> while holding both this instance's <paramref name="local"/> semaphore and the cluster
    /// lock <paramref name="name"/>, so it runs one at a time across all instances.
    /// </summary>
    public static async Task<T> OneAtATimeAsync<T>(this IClusterLocks cluster, SemaphoreSlim local, string name, Func<Task<T>> work,
        CancellationToken ct = default)
    {
        await local.WaitAsync(ct);
        try
        {
            await using var held = await cluster.AcquireAsync(name, ct);
            return await work();
        }
        finally
        {
            local.Release();
        }
    }
}
