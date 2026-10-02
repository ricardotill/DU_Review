using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace ScoutingAutomation.IntegrationTests;

/// <summary>
/// Just enough of Slack's Web API and file hosting for the app in live mode. Records every call,
/// remembers the bot's own reactions like Slack does, and serves what a test sets up.
/// </summary>
public sealed class FakeSlackApi : HttpMessageHandler
{
    /// <summary>One call to Slack: the API method (or "file" for a download) and its arguments.</summary>
    public sealed record Call(string Method, IReadOnlyDictionary<string, string> Args)
    {
        public string this[string name] => Args.TryGetValue(name, out var value) ? value : "";
    }

    public static readonly byte[] FileContent = { 1, 2, 3 };

    private readonly ConcurrentQueue<Call> _calls = new();
    private int _uploads;
    private readonly ConcurrentDictionary<(string Channel, string Ts, string Name), byte> _botReactions = new();

    /// <summary>Messages returned by conversations.history, per channel.</summary>
    public ConcurrentDictionary<string, object[]> History { get; } = new();

    /// <summary>Channels whose conversations.history fails with this Slack error.</summary>
    public ConcurrentDictionary<string, string> HistoryErrors { get; } = new();

    /// <summary>Messages returned by conversations.replies, per thread parent ts.</summary>
    public ConcurrentDictionary<string, object[]> Replies { get; } = new();

    /// <summary>Called before conversations.history answers; a test can hold the request open with it.</summary>
    public Func<string, Task>? BeforeHistory { get; set; }

    /// <summary>
    /// Reactions returned by reactions.get, per "channel/ts". Without an entry, the reactions of the message in
    /// <see cref="History"/> are returned, as Slack would; a message in neither has none.
    /// </summary>
    public ConcurrentDictionary<string, object[]> Reactions { get; } = new();

    /// <summary>Names returned by users.info, per user ID. Unknown users give "user_not_found".</summary>
    public ConcurrentDictionary<string, string> UserNames { get; } = new();

    /// <summary>File names whose download fails with 404.</summary>
    public ConcurrentDictionary<string, byte> MissingFiles { get; } = new();

    /// <summary>API methods that fail with this Slack error, e.g. "chat.postMessage" → "channel_not_found".</summary>
    public ConcurrentDictionary<string, string> Errors { get; } = new();

    /// <summary>The workspace from auth.test; null gives no workspace, so the app has no Slack links.</summary>
    public (string TeamId, string Url)? Workspace { get; set; }

    /// <summary>The bot's own user ID that auth.test gives.</summary>
    public string BotUserId { get; set; } = "UBOT";

    /// <summary>The upload address files.getUploadURLExternal gives instead of Slack's own.</summary>
    public string? UploadUrl { get; set; }

    public IReadOnlyList<Call> AllCalls => _calls.ToList();

    public IReadOnlyList<Call> Calls(string method) => _calls.Where(c => c.Method == method).ToList();

    /// <summary>Messages the bot posted, as opposed to replies in a thread.</summary>
    public IReadOnlyList<Call> Posts => Calls("chat.postMessage").Where(c => c["thread_ts"] == "").ToList();

    /// <summary>Replies the bot posted in a message's thread.</summary>
    public IReadOnlyList<Call> ThreadReplies(string ts) => Calls("chat.postMessage").Where(c => c["thread_ts"] == ts).ToList();

    /// <summary>Calls that post something to Slack, as opposed to reading from it.</summary>
    public IReadOnlyList<Call> Writes => _calls.Where(c => c.Method is "chat.postMessage" or "reactions.add" or "reactions.remove" or "files.completeUploadExternal").ToList();

    /// <summary>Files the bot shared in a channel.</summary>
    public IReadOnlyList<Call> SharedFiles => Calls("files.completeUploadExternal");

    public static string Key(string channel, string ts) => $"{channel}/{ts}";

    /// <summary>Waits until a condition on the calls holds, for work the app does in the background.</summary>
    public async Task WaitForAsync(Func<FakeSlackApi, bool> condition, string because, int timeoutMs = 10_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition(this))
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"Slack never saw: {because}");
            await Task.Delay(20);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!;
        if (url.AbsolutePath.StartsWith("/upload/"))
        {
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            _calls.Enqueue(new Call("upload", new Dictionary<string, string>
            {
                ["file_id"] = url.Segments.Last(),
                ["length"] = bytes.Length.ToString(),
                ["host"] = url.Host,
                ["authorization"] = request.Headers.Authorization?.ToString() ?? ""
            }));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("OK - " + bytes.Length) };
        }

        if (url.Host == "files.slack.com")
        {
            var name = url.Segments.Last();
            _calls.Enqueue(new Call("file", new Dictionary<string, string> { ["name"] = name }));
            return MissingFiles.ContainsKey(name)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(FileContent) };
        }

        var method = url.AbsolutePath.Replace("/api/", "");
        var args = await ArgsAsync(request, ct);
        _calls.Enqueue(new Call(method, args));

        if (Errors.TryGetValue(method, out var error))
        {
            return Json(new { ok = false, error });
        }

        string Arg(string name) => args.TryGetValue(name, out var value) ? value : "";

        switch (method)
        {
            case "conversations.history":
                if (BeforeHistory != null) await BeforeHistory(Arg("channel"));
                return HistoryErrors.TryGetValue(Arg("channel"), out var historyError)
                    ? Json(new { ok = false, error = historyError })
                    : Json(new { ok = true, messages = History.GetValueOrDefault(Arg("channel")) ?? Array.Empty<object>() });

            case "conversations.replies":
                return Json(new { ok = true, messages = Replies.GetValueOrDefault(Arg("ts")) ?? Array.Empty<object>() });

            case "reactions.get":
                return Json(new { ok = true, type = "message", message = new { reactions = Reactions.GetValueOrDefault(Key(Arg("channel"), Arg("timestamp"))) ?? HistoryReactions(Arg("channel"), Arg("timestamp")) } });

            case "reactions.add":
                return _botReactions.TryAdd((Arg("channel"), Arg("timestamp"), Arg("name")), 0)
                    ? Json(new { ok = true })
                    : Json(new { ok = false, error = "already_reacted" });

            case "reactions.remove":
                return _botReactions.TryRemove((Arg("channel"), Arg("timestamp"), Arg("name")), out _)
                    ? Json(new { ok = true })
                    : Json(new { ok = false, error = "no_reaction" });

            case "users.info":
                return UserNames.TryGetValue(Arg("user"), out var userName)
                    ? Json(new { ok = true, user = new { name = userName.ToLowerInvariant(), profile = new { display_name = userName } } })
                    : Json(new { ok = false, error = "user_not_found" });

            case "files.getUploadURLExternal":
                var fileId = "F" + Interlocked.Increment(ref _uploads);
                return Json(new { ok = true, upload_url = UploadUrl ?? $"https://files.slack.com/upload/v1/{fileId}", file_id = fileId });

            case "auth.test":
                return Workspace is { } workspace
                    ? Json(new { ok = true, team_id = workspace.TeamId, url = workspace.Url, user_id = BotUserId })
                    : Json(new { ok = false, error = "invalid_auth" });

            default:
                return Json(new { ok = true });
        }
    }

    /// <summary>Arguments from the query string and from a JSON body.</summary>
    private static async Task<Dictionary<string, string>> ArgsAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var args = new Dictionary<string, string>();
        var query = HttpUtility.ParseQueryString(request.RequestUri!.Query);
        foreach (var key in query.AllKeys.OfType<string>())
        {
            args[key] = query[key] ?? "";
        }

        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync(ct);
            if (request.Content.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded")
            {
                var form = HttpUtility.ParseQueryString(body);
                foreach (var key in form.AllKeys.OfType<string>())
                {
                    args[key] = form[key] ?? "";
                }
            }
            else if (body.StartsWith('{'))
            {
                using var json = JsonDocument.Parse(body);
                foreach (var property in json.RootElement.EnumerateObject())
                {
                    args[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                }
            }
        }

        return args;
    }

    private object HistoryReactions(string channel, string ts)
    {
        foreach (var message in History.GetValueOrDefault(channel) ?? Array.Empty<object>())
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(message);
            if (json.TryGetProperty("ts", out var t) && t.GetString() == ts && json.TryGetProperty("reactions", out var reactions))
            {
                return reactions;
            }
        }

        return Array.Empty<object>();
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
