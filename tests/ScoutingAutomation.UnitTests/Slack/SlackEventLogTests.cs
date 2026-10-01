using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackEventLogTests
{
    private static SlackEventLog Create() => new(NullLogger<SlackEventLog>.Instance);

    private static SlackEventLogEntry Entry(string detail) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.Zero, SlackEventKind.Message, "C1", "U1", null, null, 0,
        SlackEventOutcome.Processed, detail, Array.Empty<Guid>());

    [Fact]
    public void Recent_ShouldReturnNewestFirst()
    {
        var log = Create();

        log.Add(Entry("first"));
        log.Add(Entry("second"));

        log.Recent().Select(e => e.Detail).Should().Equal("second", "first");
    }

    [Fact]
    public void Add_BeyondCapacity_ShouldDropOldest()
    {
        var log = Create();

        for (var i = 0; i < SlackEventLog.Capacity + 10; i++)
        {
            log.Add(Entry(i.ToString()));
        }

        var recent = log.Recent();
        recent.Should().HaveCount(SlackEventLog.Capacity);
        recent[0].Detail.Should().Be((SlackEventLog.Capacity + 9).ToString());
        recent[^1].Detail.Should().Be("10");
    }

    [Fact]
    public void Add_ShouldNotifySubscribers_EvenWhenOneFails()
    {
        var log = Create();
        var received = new List<SlackEventLogEntry>();
        log.Added += _ => throw new InvalidOperationException();
        log.Added += received.Add;

        var entry = Entry("x");
        log.Add(entry);

        received.Should().Equal(entry);
        log.Recent().Should().Equal(entry);
    }
}
