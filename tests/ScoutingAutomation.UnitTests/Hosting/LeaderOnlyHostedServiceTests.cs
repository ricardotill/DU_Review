using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Cluster;
using Xunit;

namespace ScoutingAutomation.UnitTests.Hosting;

public class LeaderOnlyHostedServiceTests
{
    private sealed class Leadership : IClusterLeadership
    {
        public bool IsLeader { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    /// <summary>A worker that does one piece of work when told to, and never stops by itself, like a slow StopAsync.</summary>
    private sealed class Worker : IHostedService
    {
        private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool>? FencedAtWork { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            FencedAtWork = Task.Run(async () =>
            {
                await _go.Task;
                return LeaderWork.IsFenced;
            });
            return Task.CompletedTask;
        }

        public void Go() => _go.TrySetResult();

        public Task StopAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
    }

    [Fact]
    public async Task WorkOfAnOldLeader_ShouldBeRefusedAsSoonAsItIsNoLongerTheLeader()
    {
        var leadership = new Leadership { IsLeader = true };
        var worker = new Worker();
        var service = new LeaderOnlyHostedService<Worker>(worker, leadership, NullLogger<LeaderOnlyHostedService<Worker>>.Instance);
        await service.StartAsync(CancellationToken.None);

        // The worker has not been stopped yet (its StopAsync never ends), but this instance is no longer the leader
        leadership.IsLeader = false;
        worker.Go();

        (await worker.FencedAtWork!).Should().BeTrue();
        LeaderWork.IsFenced.Should().BeFalse("work outside a leader-only service, such as a page, is not fenced");
    }

    [Fact]
    public async Task WorkOfTheLeader_ShouldGoAhead()
    {
        var leadership = new Leadership { IsLeader = true };
        var worker = new Worker();
        var service = new LeaderOnlyHostedService<Worker>(worker, leadership, NullLogger<LeaderOnlyHostedService<Worker>>.Instance);
        await service.StartAsync(CancellationToken.None);

        worker.Go();

        (await worker.FencedAtWork!).Should().BeFalse();
    }
}
