using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Cluster;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Cluster;

/// <summary>The PostgreSQL parts that let several instances share one database: locks, the leader and messages.</summary>
[Collection(nameof(PostgresCollection))]
public class PostgresClusterTests(PostgresFixture postgres)
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(500);

    [SkippableFact]
    public async Task Lock_ShouldBeHeldByOneInstanceAtATime()
    {
        var db = await postgres.NewDatabaseAsync();
        await using var connectionsA = new PostgresClusterConnections(db);
        await using var connectionsB = new PostgresClusterConnections(db);
        var a = new PostgresClusterLocks(connectionsA);
        var b = new PostgresClusterLocks(connectionsB);

        var held = await a.AcquireAsync("kanaal");
        var waiting = b.AcquireAsync("kanaal");
        (await Task.WhenAny(waiting, Task.Delay(Short))).Should().NotBe(waiting, "the other instance holds it");
        (await b.AcquireAsync("ander-kanaal")).Dispose();

        await held.DisposeAsync();
        await using var got = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task SharedLock_ShouldLetLiveEventsRunTogetherButNotWithARebuild()
    {
        var db = await postgres.NewDatabaseAsync();
        await using var connections = new PostgresClusterConnections(db);
        var locks = new PostgresClusterLocks(connections);

        var first = await locks.AcquireSharedAsync(ClusterLockNames.SlackGate);
        await using (await locks.AcquireSharedAsync(ClusterLockNames.SlackGate).WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        var rebuild = locks.AcquireAsync(ClusterLockNames.SlackGate);
        (await Task.WhenAny(rebuild, Task.Delay(Short))).Should().NotBe(rebuild, "a live event is still running");
        await first.DisposeAsync();
        var paused = await rebuild.WaitAsync(TimeSpan.FromSeconds(5));

        var live = locks.AcquireSharedAsync(ClusterLockNames.SlackGate);
        (await Task.WhenAny(live, Task.Delay(Short))).Should().NotBe(live, "the rebuild pauses live events");
        await paused.DisposeAsync();
        await using var _ = await live.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task CancelledWait_ShouldNotKeepTheLock()
    {
        var db = await postgres.NewDatabaseAsync();
        await using var connections = new PostgresClusterConnections(db);
        var locks = new PostgresClusterLocks(connections);
        var held = await locks.AcquireAsync("x");
        using var cancel = new CancellationTokenSource(Short);

        var act = () => locks.AcquireAsync("x", cancel.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        await held.DisposeAsync();
        await using var _ = await locks.AcquireAsync("x").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [SkippableFact]
    public async Task Leadership_ShouldBeWithOneInstanceAndMoveWhenItStops()
    {
        var db = await postgres.NewDatabaseAsync();
        await using var connectionsA = new PostgresClusterConnections(db);
        await using var connectionsB = new PostgresClusterConnections(db);
        var interval = TimeSpan.FromMilliseconds(200);
        var a = new PostgresLeadership(connectionsA, NullLogger<PostgresLeadership>.Instance, interval);
        var b = new PostgresLeadership(connectionsB, NullLogger<PostgresLeadership>.Instance, interval);

        await a.StartAsync(CancellationToken.None);
        await WaitUntil(() => a.IsLeader);
        await b.StartAsync(CancellationToken.None);
        await Task.Delay(interval * 3);
        b.IsLeader.Should().BeFalse("there is one leader");

        await a.StopAsync(CancellationToken.None);
        a.IsLeader.Should().BeFalse();
        await WaitUntil(() => b.IsLeader);
        await b.StopAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Message_ShouldReachTheOtherInstancesButNotItself()
    {
        var db = await postgres.NewDatabaseAsync();
        await using var connectionsA = new PostgresClusterConnections(db);
        await using var connectionsB = new PostgresClusterConnections(db);
        var a = new PostgresClusterBus(connectionsA, NullLogger<PostgresClusterBus>.Instance);
        var b = new PostgresClusterBus(connectionsB, NullLogger<PostgresClusterBus>.Instance);
        var atA = new List<string>();
        var atB = new List<string>();
        a.Subscribe("test", p => { lock (atA) atA.Add(p); });
        b.Subscribe("test", p => { lock (atB) atB.Add(p); });
        await a.StartAsync(CancellationToken.None);
        await b.StartAsync(CancellationToken.None);
        // LISTEN runs in the background service: give it a moment
        await Task.Delay(Short);

        await a.PublishAsync("test", "met|streep");

        await WaitUntil(() => { lock (atB) return atB.Count == 1; });
        atB.Should().Equal("met|streep");
        atA.Should().BeEmpty("the sender raised its own events already");
        await a.StopAsync(CancellationToken.None);
        await b.StopAsync(CancellationToken.None);
    }

    internal static async Task WaitUntil(Func<bool> condition, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("Condition not met in time");
            await Task.Delay(50);
        }
    }
}
