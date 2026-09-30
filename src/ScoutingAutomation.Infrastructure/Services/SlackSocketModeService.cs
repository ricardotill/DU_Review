using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Receives Slack events over an outgoing WebSocket (Socket Mode), so the app needs no public HTTPS address.
/// Events are acknowledged immediately and processed from a queue, so slow downloads do not cause redeliveries.
/// </summary>
public class SlackSocketModeService : BackgroundService
{
    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly IOptions<ScoutingSettings> _options;
    private readonly ISlackSocketConnector _connector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SlackConnectionStatus _status;
    private readonly ILogger<SlackSocketModeService> _logger;

    public SlackSocketModeService(
        IOptions<ScoutingSettings> options,
        ISlackSocketConnector connector,
        IServiceScopeFactory scopeFactory,
        SlackConnectionStatus status,
        ILogger<SlackSocketModeService> logger)
    {
        _options = options;
        _connector = connector;
        _scopeFactory = scopeFactory;
        _status = status;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var slack = _options.Value.Slack;
        if (slack.SimulationMode || !slack.SocketMode)
        {
            _status.Set(SlackConnectionState.Disabled);
            return;
        }

        if (string.IsNullOrWhiteSpace(slack.AppToken))
        {
            _logger.LogError("Scouting:Slack:SocketMode is on but Scouting:Slack:AppToken is not set: no Slack events will be received");
            _status.Set(SlackConnectionState.Error, "App token not set");
            return;
        }

        var queue = Channel.CreateUnbounded<SlackEventEnvelopeDto>(new UnboundedChannelOptions { SingleReader = true });
        var processing = ProcessEventsAsync(queue.Reader, stoppingToken);

        var delay = MinReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _status.Set(SlackConnectionState.Connecting);
                var url = await _connector.OpenConnectionAsync(slack.AppToken, stoppingToken);

                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(url, stoppingToken);
                await ReceiveAsync(socket, queue.Writer, stoppingToken);

                // A normal disconnect (Slack refreshes connections regularly): reconnect quickly
                delay = MinReconnectDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Slack Socket Mode connection failed, retrying in {Delay}", delay);
                _status.Set(SlackConnectionState.Error, ex.Message);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
        }

        queue.Writer.TryComplete();
        await processing;
    }

    private async Task ReceiveAsync(ClientWebSocket socket, ChannelWriter<SlackEventEnvelopeDto> queue, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("Slack closed the Socket Mode connection: {Status}", result.CloseStatusDescription);
                    return;
                }

                message.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            SlackSocketModeMessage received;
            try
            {
                received = SlackSocketModeMessage.Parse(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ignored unreadable Socket Mode message");
                continue;
            }

            if (received.Acknowledgement is { } ack)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, endOfMessage: true, ct);
            }

            if (received.Event != null)
            {
                queue.TryWrite(received.Event);
            }

            if (received.IsHello)
            {
                _logger.LogInformation("Connected to Slack over Socket Mode");
                _status.Set(SlackConnectionState.Connected);
            }

            if (received.IsDisconnect)
            {
                _logger.LogInformation("Slack asked to reconnect: {Reason}", received.DisconnectReason);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", ct);
                return;
            }
        }
    }

    private async Task ProcessEventsAsync(ChannelReader<SlackEventEnvelopeDto> queue, CancellationToken ct)
    {
        // Ends when the connection loop completes the queue on shutdown
        await foreach (var envelope in queue.ReadAllAsync(CancellationToken.None))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISlackEventDispatcher>().DispatchAsync(envelope, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to process Slack event {EventType}", envelope.Event?.Type);
            }
        }
    }
}
