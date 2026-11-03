using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Slack.SocketMode;

/// <summary>
/// Receives Slack events over an outgoing WebSocket (Socket Mode), so the app needs no public HTTPS address.
/// Events are acknowledged immediately and processed from a queue per channel, so slow downloads do not cause
/// redeliveries, and one group's slow upload does not hold up the others.
/// </summary>
public class SlackSocketModeService : BackgroundService
{
    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the connection is checked. A connection dropped without a goodbye (Wi-Fi gone, laptop asleep, NAT timeout)
    /// otherwise looks open forever: no events arrive, and no catch-up runs.
    /// </summary>
    internal static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);

    /// <summary>A connection that lasted this long was healthy: a failure after it starts the back-off again.</summary>
    private static readonly TimeSpan HealthyConnection = TimeSpan.FromMinutes(1);

    private readonly IOptions<ScoutingSettings> _options;
    private readonly ISlackSocketConnector _connector;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SlackConnectionStatus _status;
    private readonly ISlackCatchUpService _catchUp;
    private readonly ILogger<SlackSocketModeService> _logger;

    public SlackSocketModeService(
        IOptions<ScoutingSettings> options,
        ISlackSocketConnector connector,
        IServiceScopeFactory scopeFactory,
        SlackConnectionStatus status,
        ISlackCatchUpService catchUp,
        ILogger<SlackSocketModeService> logger)
    {
        _options = options;
        _connector = connector;
        _scopeFactory = scopeFactory;
        _status = status;
        _catchUp = catchUp;
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
            _status.Set(SlackConnectionState.Error, "App-token niet ingesteld");
            return;
        }

        var queue = new SlackEventQueue(
            envelope => ProcessEventAsync(envelope, stoppingToken),
            // The events page already shows the failed event itself: logged under its own ID, so it does not show twice
            (envelope, ex) => _logger.LogError(EventLogLoggerProvider.AlreadyShown, ex, "Failed to process Slack event {EventType}", envelope.Event?.Type));

        var delay = MinReconnectDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            var connectedAt = DateTimeOffset.UtcNow;
            try
            {
                _status.Set(SlackConnectionState.Connecting);
                var url = await _connector.OpenConnectionAsync(slack.AppToken, stoppingToken);

                using var socket = new ClientWebSocket();
                socket.Options.KeepAliveInterval = KeepAlive;
                socket.Options.KeepAliveTimeout = KeepAlive;
                await socket.ConnectAsync(url, stoppingToken);
                connectedAt = DateTimeOffset.UtcNow;
                await ReceiveAsync(socket, queue, stoppingToken);

                // A normal disconnect (Slack refreshes connections regularly): reconnect quickly
                delay = MinReconnectDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (DateTimeOffset.UtcNow - connectedAt > HealthyConnection) delay = MinReconnectDelay;
                _logger.LogWarning(ex, "Slack Socket Mode connection failed, retrying in {Delay}", delay);
                _status.Set(SlackConnectionState.Error, ex.Message);
            }

            try
            {
                // A little randomness, so a restart of Slack's side does not have every app reconnect at once
                await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxReconnectDelay.Ticks));
        }

        // Waits for the lanes to end. Events still queued are not processed any more, as the app is stopping;
        // Slack keeps them in the channels, and the catch-up after the next start picks them up.
        await queue.CompleteAsync();
    }

    private async Task ReceiveAsync(ClientWebSocket socket, SlackEventQueue queue, CancellationToken ct)
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
                    _status.Closed($"Slack sloot de verbinding ({result.CloseStatusDescription ?? "zonder reden"})");
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

            // Another instance took over: leave this event unacknowledged, so Slack sends it to the new leader
            if (LeaderWork.IsFenced)
            {
                _logger.LogInformation("No longer the leader: closing this Socket Mode connection");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "another instance is the leader", ct);
                return;
            }

            if (received.Acknowledgement is { } ack)
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, endOfMessage: true, ct);
            }

            if (received.Error != null)
            {
                _logger.LogWarning("Ignored unreadable Slack event in envelope {EnvelopeId}: {Error}", received.EnvelopeId, received.Error);
            }

            if (received.Event != null)
            {
                queue.Enqueue(received.Event);
            }

            if (received.IsHello)
            {
                _logger.LogInformation("Connected to Slack over Socket Mode");
                _status.Set(SlackConnectionState.Connected);

                // Events sent while the app was away are not sent again: read them back from the channels
                _catchUp.Request("connected to Slack");
            }

            if (received.IsDisconnect)
            {
                _logger.LogInformation("Slack asked to reconnect: {Reason}", received.DisconnectReason);
                _status.Closed($"Slack vroeg om opnieuw te verbinden ({received.DisconnectReason ?? "zonder reden"})");
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "reconnecting", ct);
                return;
            }
        }
    }

    private async Task ProcessEventAsync(SlackEventEnvelopeDto envelope, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ISlackEventDispatcher>().DispatchAsync(envelope, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The app is stopping
        }
    }
}
