using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Runs the app in Socket Mode against a local fake of Slack's WebSocket server.
/// </summary>
public class SlackSocketModeIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string EnvelopeId = "env-123";
    private readonly string _messageTs = $"{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.4242";

    private readonly WebApplicationFactory<Program> _baseFactory;
    private readonly string _testDbPath = Path.Combine(Path.GetTempPath(), $"scouting_socket_test_{Guid.NewGuid():N}.db");
    private readonly string _testStorageDir = Path.Combine(Path.GetTempPath(), $"scouting_socket_storage_{Guid.NewGuid():N}");
    private readonly TaskCompletionSource<string> _acknowledgement = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebApplication? _fakeSlack;
    private WebApplicationFactory<Program>? _factory;

    public SlackSocketModeIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _baseFactory = factory;
    }

    public async Task InitializeAsync()
    {
        _fakeSlack = BuildFakeSlack();
        await _fakeSlack.StartAsync();
        var socketUrl = new Uri(_fakeSlack.Urls.First().Replace("http://", "ws://") + "/socket");

        Directory.CreateDirectory(_testStorageDir);
        var pointsFile = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ScoutingAutomation.Web/opdrachtenpunten.csv"));

        _factory = _baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => TestApp.UseTestSettingsOnly(config));
            builder.UseSetting("Scouting:KeysFile", Path.Combine(Path.GetTempPath(), $"scouting_keys_{Guid.NewGuid():N}.json"));
            builder.UseSetting("Scouting:RuntimeSettingsFile", Path.Combine(Path.GetTempPath(), $"scouting_rt_{Guid.NewGuid():N}.json"));
            builder.UseSetting("ConnectionStrings:scoutingdb", $"Data Source={_testDbPath}");
            builder.UseSetting("Scouting:StorageRoot", _testStorageDir);
            builder.UseSetting("Scouting:AssignmentPointsFile", pointsFile);
            builder.UseSetting("Scouting:Slack:SimulationMode", "false");
            builder.UseSetting("Scouting:Slack:SocketMode", "true");
            builder.UseSetting("Scouting:Slack:AppToken", "xapp-test");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISlackSocketConnector>();
                services.AddSingleton<ISlackSocketConnector>(new FakeConnector(socketUrl));
            });
        });
    }

    private WebApplication BuildFakeSlack()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        app.Map("/socket", async (HttpContext context) =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await SendAsync(socket, """{"type":"hello","num_connections":1}""");
            await SendAsync(socket, JsonSerializer.Serialize(new
            {
                type = "events_api",
                envelope_id = EnvelopeId,
                payload = new
                {
                    type = "event_callback",
                    @event = new
                    {
                        type = "message",
                        subtype = "file_share",
                        channel = "C01ZEEARENDEN",
                        user = "U_SCOUT",
                        text = "Opdracht 1: socket test",
                        ts = _messageTs,
                        files = new[] { new { name = "socket_test.jpg", mimetype = "image/jpeg", size = 42, url_private = "sim://socket_test.jpg" } }
                    }
                }
            }));

            var buffer = new byte[4096];
            var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
            _acknowledgement.TrySetResult(Encoding.UTF8.GetString(buffer, 0, result.Count));

            // Keep the connection open until the app disconnects
            while (socket.State == WebSocketState.Open)
            {
                var next = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (next.MessageType == WebSocketMessageType.Close) break;
            }
        });
        return app;
    }

    private static Task SendAsync(WebSocket socket, string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    [Fact]
    public async Task EventOverSocket_ShouldBeAcknowledgedAndRegistered()
    {
        // Starting the host starts the Socket Mode service
        using var scope = _factory!.Services.CreateScope();

        var ack = await _acknowledgement.Task.WaitAsync(TimeSpan.FromSeconds(15));
        JsonDocument.Parse(ack).RootElement.GetProperty("envelope_id").GetString().Should().Be(EnvelopeId);

        var repository = scope.ServiceProvider.GetRequiredService<ISubmissionRepository>();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var registered = false;
        while (!registered && DateTime.UtcNow < deadline)
        {
            registered = await repository.ExistsAsync("C01ZEEARENDEN", _messageTs, "socket_test.jpg");
            if (!registered) await Task.Delay(200);
        }

        registered.Should().BeTrue("the event received over the socket should be processed into a submission");
        _factory.Services.GetRequiredService<SlackConnectionStatus>().State.Should().Be(SlackConnectionState.Connected);
    }

    public async Task DisposeAsync()
    {
        if (_factory != null) await _factory.DisposeAsync();
        if (_fakeSlack != null) await _fakeSlack.DisposeAsync();
        try
        {
            if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
            if (Directory.Exists(_testStorageDir)) Directory.Delete(_testStorageDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private sealed class FakeConnector(Uri url) : ISlackSocketConnector
    {
        public Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct = default) => Task.FromResult(url);
    }
}
