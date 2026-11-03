using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ScoutingAutomation.Infrastructure.Cluster;

/// <summary>Connections to PostgreSQL for the cluster parts, made from the app's own connection string.</summary>
public sealed class PostgresClusterConnections : IAsyncDisposable
{
    public PostgresClusterConnections(string connectionString)
    {
        // A lock, the leadership and the listener each live as long as their own session: closing the connection is what
        // gives a lock back, also when the app crashes. A pooled connection would keep the session, and the lock, alive.
        var dedicated = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            KeepAlive = 10,
            TcpKeepAlive = true,
            ApplicationName = "du-review-cluster"
        };
        Dedicated = NpgsqlDataSource.Create(dedicated);
        Pooled = NpgsqlDataSource.Create(connectionString);
    }

    /// <summary>A new session for each connection, closed for real when disposed.</summary>
    public NpgsqlDataSource Dedicated { get; }

    /// <summary>For short statements such as sending a message.</summary>
    public NpgsqlDataSource Pooled { get; }

    /// <summary>The 64-bit advisory lock key of a lock name; the same on every instance.</summary>
    public static long Key(string name) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes("du-review:" + name)), 0);

    public async ValueTask DisposeAsync()
    {
        await Dedicated.DisposeAsync();
        await Pooled.DisposeAsync();
    }
}

/// <summary>Cluster locks as PostgreSQL advisory locks, each held by its own session.</summary>
public sealed class PostgresClusterLocks(PostgresClusterConnections connections) : IClusterLocks
{
    public bool IsDistributed => true;

    public Task<ClusterLock> AcquireAsync(string name, CancellationToken ct = default) => AcquireAsync(name, "pg_advisory_lock", ct);

    public Task<ClusterLock> AcquireSharedAsync(string name, CancellationToken ct = default) => AcquireAsync(name, "pg_advisory_lock_shared", ct);

    private async Task<ClusterLock> AcquireAsync(string name, string function, CancellationToken ct)
    {
        var connection = await connections.Dedicated.OpenConnectionAsync(ct);
        try
        {
            await using var command = new NpgsqlCommand($"SELECT {function}(@key)", connection);
            command.Parameters.AddWithValue("key", PostgresClusterConnections.Key(name));
            // Waits in PostgreSQL; a cancel stops the wait there
            await command.ExecuteNonQueryAsync(ct);
            return new SessionLock(connection);
        }
        catch
        {
            // Closing the session also gives back a lock that was granted just before the cancel came
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class SessionLock(NpgsqlConnection connection) : ClusterLock
    {
        private int _released;

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) await connection.DisposeAsync();
        }
    }
}

/// <summary>
/// The leader is the instance that holds the leader advisory lock on an open session. When that instance stops or loses
/// its connection, PostgreSQL drops the lock and another instance takes it within <see cref="DefaultCheckInterval"/>.
/// </summary>
public sealed class PostgresLeadership(PostgresClusterConnections connections, ILogger<PostgresLeadership> logger, TimeSpan? checkInterval = null)
    : BackgroundService, IClusterLeadership
{
    public static readonly TimeSpan DefaultCheckInterval = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _checkInterval = checkInterval ?? DefaultCheckInterval;

    private volatile bool _isLeader;

    public bool IsLeader => _isLeader;

    public event Action? Changed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await connections.Dedicated.OpenConnectionAsync(stoppingToken);
                while (!stoppingToken.IsCancellationRequested)
                {
                    if (!_isLeader)
                    {
                        await using var take = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                        take.Parameters.AddWithValue("key", PostgresClusterConnections.Key(ClusterLockNames.Leader));
                        if (await take.ExecuteScalarAsync(stoppingToken) is true)
                        {
                            logger.LogWarning("This instance is now the leader: it handles Slack and the scheduled messages");
                            SetLeader(true);
                        }
                    }
                    else
                    {
                        // The lock lives as long as the session; a failing check means it may be gone
                        await using var check = new NpgsqlCommand("SELECT 1", connection);
                        await check.ExecuteScalarAsync(stoppingToken);
                    }

                    await Task.Delay(_checkInterval, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_isLeader) logger.LogError(ex, "Lost the connection that holds the leadership; another instance can take over");
                else logger.LogWarning(ex, "Could not check the leadership; trying again");
                SetLeader(false);
                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        SetLeader(false);
    }

    private void SetLeader(bool leader)
    {
        if (_isLeader == leader) return;
        _isLeader = leader;
        Changed?.Invoke();
    }
}

/// <summary>Messages between instances over PostgreSQL LISTEN/NOTIFY.</summary>
public sealed class PostgresClusterBus(PostgresClusterConnections connections, ILogger<PostgresClusterBus> logger)
    : BackgroundService, IClusterBus
{
    public const string Channel = "du_review";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, ImmutableHandlers> _handlers = new();

    /// <summary>Tells this instance's own messages apart from those of the others.</summary>
    public string InstanceId { get; } = Guid.NewGuid().ToString("N")[..12];

    public event Action? Reconnected;

    public void Subscribe(string topic, Action<string> handler) =>
        _handlers.AddOrUpdate(topic, _ => new ImmutableHandlers([handler]), (_, h) => new ImmutableHandlers([.. h.All, handler]));

    public void Publish(string topic, string payload = "") => _ = PublishAsync(topic, payload);

    /// <summary>Sends the message; a failure is logged, never thrown at the caller whose change already went through.</summary>
    public async Task PublishAsync(string topic, string payload = "")
    {
        try
        {
            await using var connection = await connections.Pooled.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT pg_notify(@channel, @message)", connection);
            command.Parameters.AddWithValue("channel", Channel);
            command.Parameters.AddWithValue("message", $"{InstanceId}|{topic}|{payload}");
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not tell the other instances about {Topic}; their pages catch up at the next change", topic);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var first = true;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await connections.Dedicated.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, e) => Dispatch(e.Payload);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                // Anything sent while the connection was down is lost: whoever keeps shared data reloads it
                if (!first) Raise(Reconnected);
                first = false;
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                first = false;
                logger.LogWarning(ex, "Lost the connection for messages from the other instances; reconnecting");
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    internal void Dispatch(string message)
    {
        var parts = message.Split('|', 3);
        if (parts.Length < 3 || parts[0] == InstanceId) return;
        if (!_handlers.TryGetValue(parts[1], out var handlers)) return;

        foreach (var handler in handlers.All)
        {
            try
            {
                handler(parts[2]);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Handling {Topic} from another instance failed", parts[1]);
            }
        }
    }

    private void Raise(Action? handlers)
    {
        foreach (var handler in handlers?.GetInvocationList().Cast<Action>() ?? [])
        {
            try
            {
                handler();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reloading after a reconnect failed");
            }
        }
    }

    private sealed record ImmutableHandlers(Action<string>[] All);
}
