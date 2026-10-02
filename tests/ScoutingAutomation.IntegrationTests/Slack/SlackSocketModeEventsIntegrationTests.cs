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
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Every kind of event over Socket Mode, and catching up on connect, against a fake Slack WebSocket server and Web API.
/// </summary>
public class SlackSocketModeEventsIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C04POOLVOSSEN";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new();
    private readonly ConcurrentQueue<string> _acks = new();
    private readonly string _edited = SlackEvents.Ts(-30);
    private readonly string _deleted = SlackEvents.Ts(-20);
    private readonly string _missed = SlackEvents.Ts(-600);
    private WebApplication? _socketServer;
    private TestApp? _app;

    public SlackSocketModeEventsIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    /// <summary>What Slack sends over the socket, in order; each waits for the app's acknowledgement of the one before.</summary>
    private object[] Events => new[]
    {
        SlackEvents.Message(Channel, _edited, "Opdracht 1", SlackEvents.File("socket.jpg")),
        SlackEvents.Reaction(Channel, _edited, "thumbsup"),
        SlackEvents.Edited(Channel, _edited, "Opdracht 1", "Opdracht 2", new[] { SlackEvents.File("socket.jpg") }),
        SlackEvents.Message(Channel, _deleted, "Opdracht 3", SlackEvents.File("weg.jpg")),
        SlackEvents.Deleted(Channel, _deleted, "Opdracht 3")
    };

    public async Task InitializeAsync()
    {
        // Posted while the app was not connected; the catch-up on connect must find it
        _slack.History["C02BOSUILEN"] = new[] { SlackEvents.HistoryMessage(_missed, "Opdracht 4", new[] { SlackEvents.File("gemist.jpg") }) };

        _socketServer = BuildSocketServer();
        await _socketServer.StartAsync();
        var socketUrl = new Uri(_socketServer.Urls.First().Replace("http://", "ws://") + "/socket");

        _app = TestApp.Live(_factory, _slack,
            new Dictionary<string, string?>
            {
                ["Scouting:Slack:SocketMode"] = "true",
                ["Scouting:Slack:AppToken"] = "xapp-test"
            },
            services =>
            {
                services.RemoveAll<ISlackSocketConnector>();
                services.AddSingleton<ISlackSocketConnector>(new FakeConnector(socketUrl));
            });
    }

    private WebApplication BuildSocketServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var server = builder.Build();
        server.UseWebSockets();
        server.Map("/socket", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SendAsync(socket, """{"type":"hello","num_connections":1}""");

            var buffer = new byte[8192];
            var events = Events;
            for (var i = 0; i < events.Length; i++)
            {
                await SendAsync(socket, JsonSerializer.Serialize(new
                {
                    type = "events_api",
                    envelope_id = $"env-{i}",
                    payload = new { type = "event_callback", @event = events[i] }
                }));
                var ack = await socket.ReceiveAsync(buffer, context.RequestAborted);
                _acks.Enqueue(JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, ack.Count)).RootElement.GetProperty("envelope_id").GetString()!);
            }

            // Keep the connection open until the app disconnects
            while (socket.State == WebSocketState.Open)
            {
                var next = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (next.MessageType == WebSocketMessageType.Close) break;
            }
        });
        return server;
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private Task<IReadOnlyList<Domain.Entities.Submission>> Submissions(string channel, string ts) =>
        _app!.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(channel, ts));

    private static async Task Eventually(Func<Task<bool>> condition, string because)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException(because);
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task AllEventKinds_ShouldBeAcknowledgedAndAppliedInOrder()
    {
        // Starting the host connects to the socket
        var catchUp = _app!.Factory.Services.GetRequiredService<ISlackCatchUpService>();

        await Eventually(() => Task.FromResult(_acks.Count == Events.Length), "every envelope is acknowledged");
        _acks.Should().Equal(Enumerable.Range(0, Events.Length).Select(i => $"env-{i}"));

        await Eventually(async () => (await Submissions(Channel, _edited)).SingleOrDefault()?.AssignmentNumber == 2
                                     && (await Submissions(Channel, _deleted)).Count == 0,
            "the edit and the deletion are applied");
        var edited = (await Submissions(Channel, _edited)).Single();
        edited.Status.Should().Be(SubmissionStatus.Approved);
        Path.GetFileName(edited.StoragePath).Should().StartWith("DPV 2 ");
        // The rows are deleted before the file moves, so the file can land in "_deleted" just after the row is gone
        var deletedFile = Path.Combine(_app.StorageRoot, LocalFileStorageService.DeletedFolderName, "de-poolvossen",
            $"DPV 3 {SlackTimestamp.ToDateTimeOffset(_deleted)!.Value.ToGameTime():HH-mm-ss}.jpg");
        await Eventually(() => Task.FromResult(File.Exists(deletedFile)), "the deleted file moves to the deleted folder");

        await Eventually(async () => (await Submissions("C02BOSUILEN", _missed)).Count == 1, "the catch-up on connect finds the missed upload");
        await catchUp.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        catchUp.LastResult!.SubmissionsAdded.Should().Be(1);
        _app.Factory.Services.GetRequiredService<SlackConnectionStatus>().State.Should().Be(SlackConnectionState.Connected);
    }

    public async Task DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
        if (_socketServer != null) await _socketServer.DisposeAsync();
    }

    private sealed class FakeConnector(Uri url) : ISlackSocketConnector
    {
        public Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct = default) => Task.FromResult(url);
    }
}
