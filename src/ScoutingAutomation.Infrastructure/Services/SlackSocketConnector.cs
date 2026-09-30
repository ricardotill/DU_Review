using System.Net.Http.Headers;
using System.Text.Json;

namespace ScoutingAutomation.Infrastructure.Services;

/// <summary>
/// Asks Slack for a fresh Socket Mode WebSocket address.
/// </summary>
public interface ISlackSocketConnector
{
    Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct = default);
}

public class SlackSocketConnector : ISlackSocketConnector
{
    private readonly HttpClient _httpClient;

    public SlackSocketConnector(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/apps.connections.open");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appToken);

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = body.RootElement;
        if (!root.GetProperty("ok").GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
            throw new InvalidOperationException($"Slack refused the Socket Mode connection: {error}");
        }

        return new Uri(root.GetProperty("url").GetString()!);
    }
}
