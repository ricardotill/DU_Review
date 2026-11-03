using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Cluster;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Cluster;

/// <summary>Two instances of the real app on one PostgreSQL database, the way a server with two containers runs it.</summary>
[Collection(nameof(PostgresCollection))]
public sealed class TwoInstancesIntegrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly List<IAsyncDisposable> _disposables = [];
    private TestApp _a = null!;
    private TestApp _b = null!;

    public Task InitializeAsync() => Task.CompletedTask;

    private async Task StartAsync()
    {
        var db = await postgres.NewDatabaseAsync();
        // As Aspire and docker-compose hand the connection string to the app
        Environment.SetEnvironmentVariable("ConnectionStrings__scoutingdb", db);
        var settings = new Dictionary<string, string?> { ["ConnectionStrings:scoutingdb"] = db };
        var factoryA = new WebApplicationFactory<Program>();
        var factoryB = new WebApplicationFactory<Program>();
        _disposables.Add(factoryA);
        _disposables.Add(factoryB);
        _a = new TestApp(factoryA, settings);
        _b = new TestApp(factoryB, settings);
        _disposables.Insert(0, _a);
        _disposables.Insert(0, _b);
        // Starts both: the second waits until the first set up the database
        await Task.WhenAll(Task.Run(() => _a.Factory.Services), Task.Run(() => _b.Factory.Services));
    }

    [SkippableFact]
    public async Task SettingsChangedOnOneInstance_ShouldApplyOnTheOther()
    {
        await StartAsync();
        var a = _a.Factory.Services.GetRequiredService<RuntimeSettingsService>();
        var b = _b.Factory.Services.GetRequiredService<RuntimeSettingsService>();

        a.AddReviewer("U_GEDEELD").Should().BeNull();
        a.SetManualMode(true);

        await PostgresClusterTests.WaitUntil(() => b.IsReviewer("U_GEDEELD") && b.ManualMode);
        b.RemoveReviewer("U_GEDEELD").Should().BeTrue("b saves on top of a's change, not on an old copy");
        await PostgresClusterTests.WaitUntil(() => !a.IsReviewer("U_GEDEELD"));
    }

    [SkippableFact]
    public async Task ChangeOnOneInstance_ShouldReloadThePagesOfTheOther()
    {
        await StartAsync();
        var changed = 0;
        _b.Factory.Services.GetRequiredService<ISubmissionChangeNotifier>().Changed += () => Interlocked.Increment(ref changed);
        // LISTEN runs in the background: let it start
        await Task.Delay(500);

        _a.Factory.Services.GetRequiredService<ISubmissionChangeNotifier>().NotifyChanged();

        await PostgresClusterTests.WaitUntil(() => Volatile.Read(ref changed) == 1);
    }

    [SkippableFact]
    public async Task LoginCookie_ShouldBeReadableByBothInstances()
    {
        await StartAsync();
        var protectedOnA = _a.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("geheim");

        _b.Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(protectedOnA).Should().Be("geheim");
    }

    [SkippableFact]
    public async Task OneInstance_ShouldBeTheLeader()
    {
        await StartAsync();
        var a = _a.Factory.Services.GetRequiredService<IClusterLeadership>();
        var b = _b.Factory.Services.GetRequiredService<IClusterLeadership>();

        await PostgresClusterTests.WaitUntil(() => a.IsLeader || b.IsLeader);
        await Task.Delay(PostgresLeadership.DefaultCheckInterval);
        (a.IsLeader && b.IsLeader).Should().BeFalse();
    }

    [SkippableFact]
    public async Task RebuildOnOneInstance_ShouldPauseLiveEventsOnTheOther()
    {
        await StartAsync();
        var gateA = _a.Factory.Services.GetRequiredService<SlackProcessingGate>();
        var gateB = _b.Factory.Services.GetRequiredService<SlackProcessingGate>();
        var locksA = _a.Factory.Services.GetRequiredService<SlackChannelLocks>();
        var locksB = _b.Factory.Services.GetRequiredService<SlackChannelLocks>();

        var paused = await gateA.CloseAsync();
        var live = gateB.EnterAsync();
        (await Task.WhenAny(live, Task.Delay(500))).Should().NotBe(live);
        paused.Dispose();
        (await live.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();

        var channel = await locksA.EnterAsync("C_GROEP");
        var other = locksB.EnterAsync("C_GROEP");
        (await Task.WhenAny(other, Task.Delay(500))).Should().NotBe(other, "the channel takes turns across instances");
        channel.Dispose();
        (await other.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();
        Environment.SetEnvironmentVariable("ConnectionStrings__scoutingdb", null);
    }
}
