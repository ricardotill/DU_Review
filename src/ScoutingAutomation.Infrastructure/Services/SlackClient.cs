using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

public class SlackClient : ISlackClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SlackClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <param name="wait">Waits between download attempts; tests pass one that does not really wait.</param>
    public SlackClient(HttpClient httpClient, IOptions<ScoutingSettings> options, ILogger<SlackClient> logger,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _wait = wait ?? Task.Delay;
    }

    public async Task<Stream> DownloadFileAsync(string fileUrl, string? fileName = null, CancellationToken ct = default)
    {
        var token = _options.Value.Slack.BotToken;

        // If in simulation mode or mock url or missing token, produce simulated content
        if (_options.Value.Slack.SimulationMode || string.IsNullOrWhiteSpace(token) || fileUrl.StartsWith("sim://", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Generating simulated file stream for: {FileName} ({Url})", fileName ?? "file", fileUrl);
            return CreateSimulatedFileStream(fileName ?? "submission.jpg");
        }

        // A failed download throws: storing a placeholder would hide a lost submission
        var response = await SendDownloadRequestAsync(fileUrl, token, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            // The caller reads straight from the network, so a large video is never held in memory
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public const int DownloadAttempts = 3;

    /// <summary>
    /// Downloads are safe to repeat, so a network error, a Slack server error or a rate limit is retried a few times.
    /// Calls that post something are never retried: they could post twice.
    /// </summary>
    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string fileUrl, string token, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (attempt < DownloadAttempts)
            {
                _logger.LogWarning(ex, "Download of {Url} failed (attempt {Attempt}), retrying", fileUrl, attempt);
                await _wait(RetryDelay(attempt, null), ct);
                continue;
            }

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retry || attempt >= DownloadAttempts)
            {
                return response;
            }

            _logger.LogWarning("Download of {Url} got HTTP {Status} (attempt {Attempt}), retrying", fileUrl, (int)response.StatusCode, attempt);
            var wait = RetryDelay(attempt, response.Headers.RetryAfter?.Delta);
            response.Dispose();
            await _wait(wait, ct);
        }
    }

    /// <summary>Slack's Retry-After when given (capped), otherwise 1, 2, 4... seconds.</summary>
    private static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } wait
            ? TimeSpan.FromSeconds(Math.Min(wait.TotalSeconds, 30))
            : TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));

    /// <summary>The body of a download; disposing it also releases the HTTP response.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => inner.ReadAsync(buffer, offset, count, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct) => inner.CopyToAsync(destination, bufferSize, ct);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public async Task<bool> AddReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default)
    {
        var name = reaction.Trim(':');
        if (IsSimulated)
        {
            _logger.LogInformation("Simulated adding reaction ':{Reaction}:' to message {Ts} in {Channel}", name, messageTs, channel);
            return true;
        }

        var error = await CallSlackApiAsync("reactions.add", new { channel, timestamp = messageTs, name }, ct);
        return error != "already_reacted";
    }

    public async Task RemoveReactionAsync(string channel, string messageTs, string reaction, CancellationToken ct = default)
    {
        var name = reaction.Trim(':');
        if (IsSimulated)
        {
            _logger.LogInformation("Simulated removing reaction ':{Reaction}:' from message {Ts} in {Channel}", name, messageTs, channel);
            return;
        }

        await CallSlackApiAsync("reactions.remove", new { channel, timestamp = messageTs, name }, ct);
    }

    public async Task PostThreadReplyAsync(string channel, string threadTs, string text, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            _logger.LogInformation("Simulated thread reply on message {Ts} in {Channel}: {Text}", threadTs, channel, text);
            return;
        }

        await CallSlackApiAsync("chat.postMessage", new { channel, thread_ts = threadTs, text }, ct);
    }

    public async Task<IReadOnlyList<SlackMessageReactionDto>?> GetReactionsAsync(string channel, string messageTs, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            _logger.LogInformation("Simulation mode: reactions on message {Ts} in {Channel} cannot be read", messageTs, channel);
            return null;
        }

        try
        {
            var url = $"https://slack.com/api/reactions.get?channel={Uri.EscapeDataString(channel)}&timestamp={Uri.EscapeDataString(messageTs)}&full=true";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<SlackReactionsResponseDto>(ct);
            if (body is not { Ok: true })
            {
                _logger.LogWarning("Slack API reactions.get failed: {Error}", body?.Error ?? "unknown");
                return null;
            }

            return body.Message?.Reactions ?? new List<SlackMessageReactionDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Slack API reactions.get call failed");
            return null;
        }
    }

    public bool IsConnected => !IsSimulated;

    public async Task<SlackUserLookup> GetUserNameAsync(string userId, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            return SlackUserLookup.Failed("simulation_mode");
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/users.info?user={Uri.EscapeDataString(userId)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<SlackUserInfoResponseDto>(ct);
            if (body is not { Ok: true })
            {
                return SlackUserLookup.Failed(body?.Error ?? "unknown");
            }

            var user = body.User;
            var name = new[] { user?.Profile?.DisplayName, user?.Profile?.RealName, user?.RealName, user?.Name }
                .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            return name == null ? SlackUserLookup.Failed("no_name") : SlackUserLookup.Found(name.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Slack API users.info call failed");
            return SlackUserLookup.Failed("request_failed");
        }
    }

    private bool IsSimulated =>
        _options.Value.Slack.SimulationMode || string.IsNullOrWhiteSpace(_options.Value.Slack.BotToken);

    /// <returns>The Slack error code, or null on success.</returns>
    private async Task<string?> CallSlackApiAsync(string method, object payload, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            // Slack reports API errors with HTTP 200 and "ok": false
            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (body.RootElement.GetProperty("ok").GetBoolean())
            {
                return null;
            }

            var error = body.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown" : "unknown";
            if (error is not ("already_reacted" or "no_reaction"))
            {
                _logger.LogWarning("Slack API {Method} failed: {Error}", method, error);
            }
            return error;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Slack API {Method} call failed", method);
            return "request_failed";
        }
    }

    internal static Stream CreateSimulatedFileStream(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".gif")
        {
            // Minimal valid 1x1 PNG byte array
            var pngBytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, // PNG header
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, // IHDR chunk
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, // IDAT chunk
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, // IEND chunk
                0x42, 0x60, 0x82
            };
            return new MemoryStream(pngBytes);
        }

        // Generic mock content
        var content = $"Scouting Submission File: {fileName}\nTimestamp: {DateTimeOffset.UtcNow:O}";
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}
