using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Slack.Events;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

public class UnlockNotifierGateTests
{
    [Fact]
    public async Task Notify_WhileARebuildRuns_ShouldWaitAndReadNothing()
    {
        // A rebuild's half-imported check-ins would give a wrong count that is then posted and kept
        var gate = new SlackProcessingGate();
        var scopes = Substitute.For<IServiceScopeFactory>();
        var settings = new ScoutingSettings { Adventurers = new AdventurersSettings { Enabled = true } };
        var notifier = new UnlockNotifier(scopes, TestRuntime.For(Options.Create(settings)), NullLogger<UnlockNotifier>.Instance, gate: gate);
        using var rebuild = await gate.CloseAsync();
        using var cancel = new CancellationTokenSource();

        var notify = notifier.NotifyAsync(Guid.NewGuid(), cancel.Token);
        await Task.Delay(100);

        notify.IsCompleted.Should().BeFalse("it waits for the rebuild");
        scopes.DidNotReceive().CreateScope();
        await cancel.CancelAsync();
        await notify.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }
}
