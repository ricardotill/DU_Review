using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Services;
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
}
