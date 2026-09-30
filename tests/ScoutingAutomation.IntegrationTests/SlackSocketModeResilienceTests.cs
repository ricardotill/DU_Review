using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Socket Mode against a fake Slack that misbehaves: disconnects, split frames, garbage and closed connections.
/// </summary>
public class SlackSocketModeResilienceTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly ConcurrentQueue<string> _acks = new();
    private readonly string _ts = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.7777";
    private int _connections;
    private WebApplication? _fakeSlack;
    private TestApp? _app;

    public SlackSocketModeResilienceTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    private string Event(string envelopeId, string channel) => JsonSerializer.Serialize(new
    {
        type = "events_api",
        envelope_id = envelopeId,
        payload = new
        {
            type = "event_callback",
            @event = new
            {
                type = "message",
                channel,
                user = "U_SCOUT",
                text = "Opdracht 2",
                ts = _ts,
                files = new[] { new { name = "socket.jpg", mimetype = "image/jpeg", size = 1, url_private = "sim://socket.jpg" } }
            }
        }
    });

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _fakeSlack = builder.Build();
        _fakeSlack.UseWebSockets();
        _fakeSlack.Map("/socket", async (HttpContext context) =>
        {
            var connection = Interlocked.Increment(ref _connections);
            using var socket = await context.WebSockets.AcceptWebSocketAsync();

            switch (connection)
            {
                case 1:
                    // Garbage is skipped, then Slack asks for a reconnect
                    await SendAsync(socket, """{"type":"hello"}""");
                    await SendAsync(socket, "this is not json");
                    await SendAsync(socket, """{"type":"disconnect","reason":"refresh_requested"}""");
                    await DrainAsync(socket, context.RequestAborted);
                    break;
                case 2:
                    // A failing event must not stop the next one; the real event arrives in two frames
                    await SendAsync(socket, """{"type":"hello"}""");
                    await SendAsync(socket, Event("env-boom", "C_BOOM"));
                    await ReceiveAckAsync(socket);
                    var json = Encoding.UTF8.GetBytes(Event("env-real", "C04POOLVOSSEN"));
                    await socket.SendAsync(json.AsMemory(0, json.Length / 2), WebSocketMessageType.Text, endOfMessage: false, CancellationToken.None);
                    await socket.SendAsync(json.AsMemory(json.Length / 2), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                    await ReceiveAckAsync(socket);
                    // Slack drops the connection without warning
                    await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "going away", CancellationToken.None);
                    break;
                default:
                    await SendAsync(socket, """{"type":"hello"}""");
                    await DrainAsync(socket, context.RequestAborted);
                    break;
            }
        });
        await _fakeSlack.StartAsync();
        var socketUrl = new Uri(_fakeSlack.Urls.First().Replace("http://", "ws://") + "/socket");

        _app = new TestApp(_baseFactory,
            new Dictionary<string, string?>
            {
                ["Scouting:Slack:SimulationMode"] = "false",
                ["Scouting:Slack:SocketMode"] = "true",
                ["Scouting:Slack:AppToken"] = "xapp-test"
            },
            services =>
            {
                services.RemoveAll<ISlackSocketConnector>();
                services.AddSingleton<ISlackSocketConnector>(new FakeConnector(socketUrl));
                services.AddScoped<SlackEventDispatcher>();
                services.RemoveAll<ISlackEventDispatcher>();
                services.AddScoped<ISlackEventDispatcher, ExplodingDispatcher>();
            });
    }

    private static Task SendAsync(WebSocket socket, string text) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    private async Task ReceiveAckAsync(WebSocket socket)
    {
        var buffer = new byte[1024];
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);
        _acks.Enqueue(JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.GetProperty("envelope_id").GetString()!);
    }

    private static async Task DrainAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception)
        {
            // Connection ended
        }
    }

    [Fact]
    public async Task Service_ShouldRecoverFromEveryProblemAndStillProcessEvents()
    {
        using var scope = _app!.Factory.Services.CreateScope();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        var registered = false;
        while ((!registered || _connections < 3) && DateTime.UtcNow < deadline)
        {
            registered = await scope.ServiceProvider.GetRequiredService<ISubmissionRepository>().ExistsAsync("C04POOLVOSSEN", _ts, "socket.jpg");
            await Task.Delay(100);
        }

        _acks.Should().Equal("env-boom", "env-real");
        registered.Should().BeTrue("the split event after the failing one is still processed");
        _connections.Should().BeGreaterThanOrEqualTo(3, "the app reconnects after a disconnect request and after a dropped connection");
    }

    public async Task DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
        if (_fakeSlack != null) await _fakeSlack.DisposeAsync();
    }

    private sealed class FakeConnector(Uri url) : ISlackSocketConnector
    {
        public Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct = default) => Task.FromResult(url);
    }

    private sealed class ExplodingDispatcher(SlackEventDispatcher inner) : ISlackEventDispatcher
    {
        public Task<object?> DispatchAsync(SlackEventEnvelopeDto envelope, CancellationToken ct = default) =>
            envelope.Event?.Channel == "C_BOOM"
                ? throw new InvalidOperationException("boom")
                : inner.DispatchAsync(envelope, ct);
    }
}
