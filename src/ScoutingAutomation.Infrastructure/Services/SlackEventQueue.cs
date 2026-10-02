using System.Threading.Channels;
using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Queues Slack events with one line per Slack channel. Events of one channel are handled in the order they arrived,
/// so a reaction never runs before its message is saved. Channels do not wait for each other:
/// a large video from one group does not hold up the uploads of other groups or the reviews on them.
/// </summary>
public sealed class SlackEventQueue
{
    private readonly Func<SlackEventEnvelopeDto, Task> _process;
    private readonly Dictionary<string, Lane> _lanes = new();
    private readonly object _lock = new();
    private bool _completed;

    /// <param name="process">Handles one event. An exception is reported to <paramref name="onError"/>; the lane goes on with the next event.</param>
    public SlackEventQueue(Func<SlackEventEnvelopeDto, Task> process, Action<SlackEventEnvelopeDto, Exception>? onError = null)
    {
        _process = async envelope =>
        {
            try
            {
                await process(envelope);
            }
            catch (Exception ex)
            {
                // One failing event must never stop its channel: every later event of the group would be lost
                onError?.Invoke(envelope, ex);
            }
        };
    }

    /// <summary>The channel an event belongs to: the message's channel, or the channel of the message reacted to.</summary>
    public static string LaneFor(SlackEventEnvelopeDto envelope) =>
        envelope.Event?.Channel ?? envelope.Event?.Item?.Channel ?? "";

    /// <returns>False after <see cref="CompleteAsync"/>.</returns>
    public bool Enqueue(SlackEventEnvelopeDto envelope)
    {
        lock (_lock)
        {
            if (_completed) return false;

            var key = LaneFor(envelope);
            if (!_lanes.TryGetValue(key, out var lane))
            {
                lane = new Lane(_process);
                _lanes[key] = lane;
            }

            return lane.Writer.TryWrite(envelope);
        }
    }

    /// <summary>Accepts no new events and waits until every queued event is handled.</summary>
    public Task CompleteAsync()
    {
        lock (_lock)
        {
            _completed = true;
            foreach (var lane in _lanes.Values)
            {
                lane.Writer.TryComplete();
            }

            return Task.WhenAll(_lanes.Values.Select(l => l.Processing));
        }
    }

    private sealed class Lane
    {
        private readonly Channel<SlackEventEnvelopeDto> _channel =
            Channel.CreateUnbounded<SlackEventEnvelopeDto>(new UnboundedChannelOptions { SingleReader = true });

        public Lane(Func<SlackEventEnvelopeDto, Task> process)
        {
            Processing = Task.Run(async () =>
            {
                await foreach (var envelope in _channel.Reader.ReadAllAsync())
                {
                    await process(envelope);
                }
            });
        }

        public ChannelWriter<SlackEventEnvelopeDto> Writer => _channel.Writer;
        public Task Processing { get; }
    }
}
