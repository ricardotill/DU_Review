using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Cluster;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

public class SubmissionChangeNotifierTests
{
    [Fact]
    public void NotifyChanged_FailingHandler_ShouldStillNotifyOthers()
    {
        // Arrange
        var notifier = new SubmissionChangeNotifier(NullLogger<SubmissionChangeNotifier>.Instance);
        var notified = 0;
        notifier.Changed += () => throw new InvalidOperationException("page gone");
        notifier.Changed += () => notified++;

        // Act
        var act = () => notifier.NotifyChanged();

        // Assert
        act.Should().NotThrow();
        notified.Should().Be(1);
    }

    [Fact]
    public void NotifyChanged_AfterUnsubscribe_ShouldNotCallHandler()
    {
        var notifier = new SubmissionChangeNotifier(NullLogger<SubmissionChangeNotifier>.Instance);
        var notified = 0;
        Action handler = () => notified++;
        notifier.Changed += handler;
        notifier.Changed -= handler;

        notifier.NotifyChanged();

        notified.Should().Be(0);
    }

    [Fact]
    public void NotifyUnlocked_ShouldReachThePagesOfTheOtherInstances()
    {
        // Two instances of the app on one bus: the group's page may be open on either
        var bus = new SharedBus();
        var here = new SubmissionChangeNotifier(NullLogger<SubmissionChangeNotifier>.Instance, bus.For("here"));
        var there = new SubmissionChangeNotifier(NullLogger<SubmissionChangeNotifier>.Instance, bus.For("there"));
        var heard = new List<AssignmentsUnlockedNotification>();
        here.Unlocked += heard.Add;
        there.Unlocked += heard.Add;

        here.NotifyUnlocked(new AssignmentsUnlockedNotification(Guid.Empty, new[] { "31", "32" }, 32, 160));

        heard.Should().HaveCount(2, "once on each instance");
        heard[1].NewCodes.Should().Equal("31", "32");
        heard[1].OpenCount.Should().Be(32);
    }

    /// <summary>A bus between instances in one process: a message reaches every instance but the sender.</summary>
    private sealed class SharedBus
    {
        private readonly List<(string Instance, string Topic, Action<string> Handler)> _handlers = new();

        public IClusterBus For(string instance) => new Member(this, instance);

        private sealed class Member(SharedBus bus, string instance) : IClusterBus
        {
            public event Action? Reconnected { add { } remove { } }

            public void Publish(string topic, string payload = "")
            {
                foreach (var (_, _, handler) in bus._handlers.Where(h => h.Instance != instance && h.Topic == topic).ToList()) handler(payload);
            }

            public void Subscribe(string topic, Action<string> handler) => bus._handlers.Add((instance, topic, handler));
        }
    }
}
