using System.Text;
using System.Text.Json;
using FluentAssertions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests;

public class SlackSocketModeMessageTests
{
    private static SlackSocketModeMessage Parse(string json) => SlackSocketModeMessage.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_Hello_ShouldNotNeedAcknowledgement()
    {
        var message = Parse("""{"type":"hello","num_connections":1}""");

        message.IsHello.Should().BeTrue();
        message.Acknowledgement.Should().BeNull();
        message.Event.Should().BeNull();
    }

    [Fact]
    public void Parse_EventsApi_ShouldExposeEventAndAcknowledgement()
    {
        var message = Parse("""
        {
          "type": "events_api",
          "envelope_id": "57d6a792-4d35-4d0b-b6aa-3361493e1caf",
          "accepts_response_payload": false,
          "payload": {
            "type": "event_callback",
            "event": {
              "type": "message",
              "subtype": "file_share",
              "channel": "C01ZEEARENDEN",
              "user": "U123",
              "text": "Opdracht 4",
              "ts": "1720000000.0001",
              "files": [ { "name": "foto.jpg", "mimetype": "image/jpeg", "size": 10 } ]
            }
          }
        }
        """);

        message.Event.Should().NotBeNull();
        message.Event!.Type.Should().Be("event_callback");
        message.Event.Event!.Channel.Should().Be("C01ZEEARENDEN");
        message.Event.Event.Files.Should().ContainSingle(f => f.Name == "foto.jpg");

        using var ack = JsonDocument.Parse(message.Acknowledgement!);
        ack.RootElement.GetProperty("envelope_id").GetString().Should().Be("57d6a792-4d35-4d0b-b6aa-3361493e1caf");
    }

    [Fact]
    public void Parse_SlashCommand_ShouldBeAcknowledgedButNotProcessed()
    {
        var message = Parse("""{"type":"slash_commands","envelope_id":"abc","payload":{"command":"/test"}}""");

        message.Acknowledgement.Should().NotBeNull();
        message.Event.Should().BeNull();
    }

    [Fact]
    public void Parse_Disconnect_ShouldRequestReconnect()
    {
        var message = Parse("""{"type":"disconnect","reason":"refresh_requested"}""");

        message.IsDisconnect.Should().BeTrue();
        message.DisconnectReason.Should().Be("refresh_requested");
    }
}
