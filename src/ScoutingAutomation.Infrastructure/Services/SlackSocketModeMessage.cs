using System.Text.Json;
using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// One message received over a Socket Mode connection.
/// See https://api.slack.com/apis/socket-mode
/// </summary>
public sealed record SlackSocketModeMessage(string Type, string? EnvelopeId, SlackEventEnvelopeDto? Event, string? DisconnectReason)
{
    /// <summary>Why the event in the envelope could not be read, or null. The envelope is still acknowledged.</summary>
    public string? Error { get; init; }

    public bool IsHello => Type == "hello";
    public bool IsDisconnect => Type == "disconnect";

    /// <summary>Slack expects every envelope to be acknowledged within 3 seconds, or it sends it again.</summary>
    public string? Acknowledgement => EnvelopeId == null ? null : JsonSerializer.Serialize(new { envelope_id = EnvelopeId });

    public static SlackSocketModeMessage Parse(ReadOnlySpan<byte> json)
    {
        using var doc = JsonDocument.Parse(json.ToArray());
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        var envelopeId = root.TryGetProperty("envelope_id", out var id) ? id.GetString() : null;
        var reason = root.TryGetProperty("reason", out var r) ? r.GetString() : null;

        // Only Events API envelopes carry events; slash commands and interactions are acknowledged and ignored
        // An unreadable event is still acknowledged: Slack would only send the same unreadable event again
        SlackEventEnvelopeDto? envelope = null;
        string? error = null;
        if (type == "events_api" && root.TryGetProperty("payload", out var payload))
        {
            try
            {
                envelope = payload.Deserialize<SlackEventEnvelopeDto>(JsonSerializerOptions.Web);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
            {
                error = ex.Message;
            }
        }

        return new SlackSocketModeMessage(type, envelopeId, envelope, reason) { Error = error };
    }
}
