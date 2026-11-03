using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Infrastructure.Cluster;

namespace ScoutingAutomation.Infrastructure.Slack;

public class SlackClient : ISlackClient
{
    private readonly HttpClient _httpClient;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SlackClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;
    private readonly SlackOwnUploads? _ownUploads;

    /// <param name="wait">Waits between download attempts; tests pass one that does not really wait.</param>
    /// <param name="ownUploads">Remembers the pictures this app uploads, so they are never taken for submissions.</param>
    public SlackClient(HttpClient httpClient, IOptions<ScoutingSettings> options, ILogger<SlackClient> logger,
        Func<TimeSpan, CancellationToken, Task>? wait = null, SlackOwnUploads? ownUploads = null)
    {
        _ownUploads = ownUploads;
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _wait = wait ?? Task.Delay;
    }

    public const int DownloadAttempts = 3;

    /// <summary>Slack's own upload limit; a larger body is not a Slack file.</summary>
    public const long MaxDownloadBytes = 1L << 30;

    /// <summary>A download that receives nothing for this long is given up, so one stalled video cannot hold up its group.</summary>
    public TimeSpan DownloadIdleTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Time for one Web API call, including reading its answer.</summary>
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Attempts of one Web API call that Slack rate limits.</summary>
    public const int ApiAttempts = 3;

    public async Task<Stream> DownloadFileAsync(string fileUrl, string? fileName = null, CancellationToken ct = default)
    {
        var token = _options.Value.Slack.BotToken;

        if (_options.Value.Slack.SimulationMode || string.IsNullOrWhiteSpace(token))
        {
            _logger.LogInformation("Generating simulated file stream for: {FileName} ({Url})", fileName ?? "file", fileUrl);
            return CreateSimulatedFileStream(fileName ?? "submission.jpg");
        }

        // The bot token only goes to Slack itself; a file without a Slack address cannot be fetched
        if (!IsSlackUrl(fileUrl, out _))
        {
            throw new HttpRequestException($"Not a Slack file address: {fileUrl}");
        }

        // A failed download throws: storing a placeholder would hide a lost submission
        var response = await SendDownloadRequestAsync(fileUrl, token, ct);
        try
        {
            response.EnsureSuccessStatusCode();

            // Without the files:read scope, or with an expired link, Slack answers 200 with its login page
            if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException($"Slack sent a web page instead of the file {fileName}; check the files:read scope");
            }

            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new HttpRequestException($"File {fileName} is larger than {MaxDownloadBytes} bytes");
            }

            // The caller reads straight from the network, so a large video is never held in memory
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct), DownloadIdleTimeout);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Downloads are safe to repeat, so a network error, a Slack server error or a rate limit is retried a few times.
    /// Calls that post something are never retried: they could post twice.
    /// </summary>
    private async Task<HttpResponseMessage> SendDownloadRequestAsync(string fileUrl, string token, CancellationToken ct)
    {
        LeaderWork.ThrowIfFenced();
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(DownloadIdleTimeout);
                try
                {
                    response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Slack did not answer within {DownloadIdleTimeout.TotalSeconds:0} s", ex);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException && attempt < DownloadAttempts)
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

    /// <summary>
    /// The body of a download; disposing it also releases the HTTP response. A read that gets no bytes
    /// for <paramref name="idleTimeout"/> throws <see cref="TimeoutException"/>, and so does a body over <see cref="MaxDownloadBytes"/>.
    /// </summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream inner, TimeSpan idleTimeout) : Stream
    {
        private long _read;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(idleTimeout);
            try
            {
                return Count(await inner.ReadAsync(buffer, idle.Token));
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"The download received nothing for {idleTimeout.TotalSeconds:0} s", ex);
            }
        }

        private int Count(int read)
        {
            _read += read;
            if (_read > MaxDownloadBytes) throw new IOException($"The download is larger than {MaxDownloadBytes} bytes");
            return read;
        }

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

        // Only a reaction that is really on the message counts as feedback given: after a passing failure,
        // the next catch-up tries again instead of the scout getting the same reply each time
        var error = await CallSlackApiAsync("reactions.add", new { channel, timestamp = messageTs, name }, ct);
        return error is not ("already_reacted" or RateLimited or RequestFailed or Timeout);
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

    public async Task<string?> PostMessageAsync(string channel, string text, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            _logger.LogInformation("Simulated message in {Channel}: {Text}", channel, text);
            return "simulation_mode";
        }

        return await CallSlackApiAsync("chat.postMessage", new { channel, text }, ct);
    }

    public async Task<string?> PostFileAsync(string channel, byte[] content, string fileName, string? text, CancellationToken ct = default)
    {
        if (IsSimulated)
        {
            _logger.LogInformation("Simulated file {FileName} ({Bytes} bytes) in {Channel}: {Text}", fileName, content.Length, channel, text);
            return "simulation_mode";
        }

        // Slack's upload flow: ask for an upload address, send the bytes there, then share the file in the channel
        // https://api.slack.com/messaging/files#uploading_files
        var (error, upload) = await CallSlackFormApiAsync("files.getUploadURLExternal", new Dictionary<string, string>
        {
            ["filename"] = fileName,
            ["length"] = content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, ct);
        if (error != null) return error;

        var fileId = upload.TryGetProperty("file_id", out var id) ? id.GetString() : null;
        var uploadUrl = upload.TryGetProperty("upload_url", out var url) ? url.GetString() : null;
        if (string.IsNullOrWhiteSpace(fileId) || !IsSlackUrl(uploadUrl, out var uploadUri))
        {
            _logger.LogWarning("Slack gave no usable upload address for {FileName}: {Url}", fileName, uploadUrl);
            return "invalid_upload_url";
        }

        // Remembered before sharing: Slack may send the message back before this call returns
        _ownUploads?.Add(fileId);

        try
        {
            // The address itself grants the upload, so the bot token does not go along
            using var request = new HttpRequestMessage(HttpMethod.Post, uploadUri) { Content = new ByteArrayContent(content) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout * 4);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Upload of {FileName} to Slack failed", fileName);
            return "upload_failed";
        }

        var complete = new Dictionary<string, string>
        {
            ["files"] = JsonSerializer.Serialize(new[] { new { id = fileId, title = fileName } }),
            ["channel_id"] = channel
        };
        if (!string.IsNullOrWhiteSpace(text)) complete["initial_comment"] = text;

        (error, _) = await CallSlackFormApiAsync("files.completeUploadExternal", complete, ct);
        return error;
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
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Get, url), read: true, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<SlackReactionsResponseDto>(ct);
            if (body is not { Ok: true })
            {
                _logger.LogWarning("Slack API reactions.get failed: {Error}", body?.Error ?? "unknown");
                return null;
            }

            return body.Message?.Reactions ?? new List<SlackMessageReactionDto>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
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
            using var response = await SendApiAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"https://slack.com/api/users.info?user={Uri.EscapeDataString(userId)}"), read: true, ct);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Slack API users.info call failed");
            return SlackUserLookup.Failed(RequestFailed);
        }
    }

    public async Task<SlackWorkspace?> GetWorkspaceAsync(CancellationToken ct = default)
    {
        if (IsSimulated) return null;

        try
        {
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/auth.test"), read: true, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<SlackAuthTestResponseDto>(ct);
            if (body is not { Ok: true } || string.IsNullOrWhiteSpace(body.TeamId) || string.IsNullOrWhiteSpace(body.Url))
            {
                _logger.LogWarning("Slack API auth.test failed: {Error}", body?.Error ?? "no team or url");
                return null;
            }

            return new SlackWorkspace(body.TeamId, body.Url.EndsWith('/') ? body.Url : body.Url + "/",
                string.IsNullOrWhiteSpace(body.UserId) ? null : body.UserId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Slack API auth.test call failed");
            return null;
        }
    }

    private bool IsSimulated =>
        _options.Value.Slack.SimulationMode || string.IsNullOrWhiteSpace(_options.Value.Slack.BotToken);

    public const string RateLimited = "ratelimited";
    public const string RequestFailed = "request_failed";
    public const string Timeout = "timeout";

    /// <summary>
    /// Sends one Web API call with the bot token and a time limit. Slack did not handle a rate-limited call,
    /// so that is retried after Slack's Retry-After even when it posts something. Network and server errors
    /// are only retried for a <paramref name="read"/>, which is safe to repeat.
    /// </summary>
    /// <exception cref="TimeoutException">Slack did not answer within <see cref="ApiTimeout"/>.</exception>
    private async Task<HttpResponseMessage> SendApiAsync(Func<HttpRequestMessage> create, bool read, CancellationToken ct)
    {
        LeaderWork.ThrowIfFenced();
        for (var attempt = 1; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);

            HttpResponseMessage response;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ApiTimeout);
                try
                {
                    // The default completion option reads the whole answer, so the time limit covers it too
                    response = await _httpClient.SendAsync(request, timeout.Token);
                }
                catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Slack did not answer {request.RequestUri} within {ApiTimeout.TotalSeconds:0} s", ex);
                }
            }
            catch (Exception ex) when (read && ex is HttpRequestException or TimeoutException && attempt < ApiAttempts)
            {
                _logger.LogWarning(ex, "Slack API {Url} failed (attempt {Attempt}), retrying", request.RequestUri, attempt);
                await _wait(RetryDelay(attempt, null), ct);
                continue;
            }

            var retry = response.StatusCode == HttpStatusCode.TooManyRequests || (read && (int)response.StatusCode >= 500);
            if (!retry || attempt >= ApiAttempts)
            {
                return response;
            }

            _logger.LogWarning("Slack API {Url} got HTTP {Status} (attempt {Attempt}), retrying", request.RequestUri, (int)response.StatusCode, attempt);
            var wait = RetryDelay(attempt, response.Headers.RetryAfter?.Delta);
            response.Dispose();
            await _wait(wait, ct);
        }
    }

    /// <summary>The error code for a call that threw.</summary>
    private static string ErrorFor(Exception ex) => ex is TimeoutException ? Timeout : RequestFailed;

    /// <returns>The Slack error code, or null on success.</returns>
    private async Task<string?> CallSlackApiAsync(string method, object payload, CancellationToken ct)
    {
        try
        {
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}")
            {
                Content = JsonContent.Create(payload)
            }, read: false, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Slack API {Method} is still rate limited", method);
                return RateLimited;
            }
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
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Slack API {Method} call failed", method);
            return ErrorFor(ex);
        }
    }

    /// <summary>Only an https address on Slack's own domain gets the picture or the bot token.</summary>
    internal static bool IsSlackUrl(string? url, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Uri? uri) =>
        Uri.TryCreate(url, UriKind.Absolute, out uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host == "slack.com" || uri.Host.EndsWith(".slack.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>A call with form fields, for methods that do not accept JSON, such as the file upload methods.</summary>
    /// <returns>The Slack error code, or null with the response body on success.</returns>
    private async Task<(string? Error, JsonElement Body)> CallSlackFormApiAsync(string method, IDictionary<string, string> fields, CancellationToken ct)
    {
        try
        {
            using var response = await SendApiAsync(() => new HttpRequestMessage(HttpMethod.Post, $"https://slack.com/api/{method}")
            {
                Content = new FormUrlEncodedContent(fields)
            }, read: false, ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning("Slack API {Method} is still rate limited", method);
                return (RateLimited, default);
            }
            response.EnsureSuccessStatusCode();

            using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (body.RootElement.GetProperty("ok").GetBoolean())
            {
                return (null, body.RootElement.Clone());
            }

            var error = body.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? "unknown" : "unknown";
            _logger.LogWarning("Slack API {Method} failed: {Error}", method, error);
            return (error, default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Slack API {Method} call failed", method);
            return (ErrorFor(ex), default);
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
