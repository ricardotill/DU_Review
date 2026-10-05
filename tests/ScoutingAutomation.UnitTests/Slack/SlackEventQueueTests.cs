using System.Collections.Concurrent;
using FluentAssertions;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Infrastructure.Slack.Events;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackEventQueueTests
{
    private static SlackEventEnvelopeDto Event(string channel, string type = "message", string? ts = null) => new()
    {
        Type = "event_callback",
        Event = type == "message"
            ? new SlackEventItemDto { Type = type, Channel = channel, Ts = ts }
            : new SlackEventItemDto { Type = type, Item = new SlackReactionTargetDto { Channel = channel, Ts = ts } }
    };

    [Fact]
    public async Task SlowEventInOneChannel_ShouldNotHoldUpAnotherChannel()
    {
        var slowVideo = new TaskCompletionSource();
        var otherDone = new TaskCompletionSource();
        var queue = new SlackEventQueue(async e =>
        {
            if (e.Event!.Channel == "C_VIDEO") await slowVideo.Task;
            else otherDone.TrySetResult();
        });

        queue.Enqueue(Event("C_VIDEO"));
        queue.Enqueue(Event("C_PHOTO"));

        await otherDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
        slowVideo.SetResult();
        await queue.CompleteAsync();
    }

    [Fact]
    public async Task EventsOfOneChannel_ShouldRunInOrderOneAtATime()
    {
        var handled = new ConcurrentQueue<string>();
        var running = 0;
        var overlap = false;
        var queue = new SlackEventQueue(async e =>
        {
            if (Interlocked.Increment(ref running) > 1) overlap = true;
            await Task.Delay(5);
            handled.Enqueue($"{e.Event!.Type}:{e.Event.Ts ?? e.Event.Item!.Ts}");
            Interlocked.Decrement(ref running);
        });

        // A reaction on a message is queued behind that message, also though it names the channel elsewhere
        queue.Enqueue(Event("C1", ts: "1"));
        queue.Enqueue(Event("C1", "reaction_added", "1"));
        queue.Enqueue(Event("C1", ts: "2"));
        await queue.CompleteAsync();

        handled.Should().Equal("message:1", "reaction_added:1", "message:2");
        overlap.Should().BeFalse();
    }

    [Fact]
    public async Task Complete_ShouldFinishQueuedEventsAndRefuseNewOnes()
    {
        var count = 0;
        var queue = new SlackEventQueue(async _ => { await Task.Delay(10); Interlocked.Increment(ref count); });
        queue.Enqueue(Event("C1"));
        queue.Enqueue(Event("C2"));

        await queue.CompleteAsync();

        count.Should().Be(2);
        queue.Enqueue(Event("C1")).Should().BeFalse();
    }
}
