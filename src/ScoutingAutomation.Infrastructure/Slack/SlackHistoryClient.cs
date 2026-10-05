using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Slack;

/// <summary>
/// Reads messages back from Slack, for rebuilding the app's data.
/// </summary>
public interface ISlackHistoryClient
{
    /// <summary>
    /// All messages posted in a channel since <paramref name="oldest"/>, including thread replies, oldest first.
    /// Each message's <see cref="SlackEventItemDto.Channel"/> is set.
    /// </summary>
    Task<IReadOnlyList<SlackEventItemDto>> GetChannelMessagesAsync(string channelId, DateTimeOffset oldest, CancellationToken ct = default);
}

public class SlackHistoryClient : ISlackHistoryClient
{
    private const int PageSize = 200;
    private const int MaxAttempts = 5;

    /// <summary>The longest wait for one rate limit; Slack asks for a few seconds up to a minute.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SlackHistoryClient> _logger;
    private readonly RuntimeSettingsService? _runtime;
    private readonly SlackLinks? _links;
    private readonly Func<TimeSpan, CancellationToken, Task> _wait;

    /// <param name="runtime">The reviewers; threads with only their replies are not read.</param>
    /// <param name="links">The bot's user ID; threads with only the bot's feedback are not read.</param>
    /// <param name="wait">Waits between attempts; tests pass one that does not really wait.</param>
    public SlackHistoryClient(HttpClient httpClient, IOptions<ScoutingSettings> options, ILogger<SlackHistoryClient> logger,
        RuntimeSettingsService? runtime = null, SlackLinks? links = null, Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
        _runtime = runtime;
        _links = links;
        _wait = wait ?? Task.Delay;
    }

    /// <summary>Time for reading one page.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public async Task<IReadOnlyList<SlackEventItemDto>> GetChannelMessagesAsync(string channelId, DateTimeOffset oldest, CancellationToken ct = default)
    {
        var oldestTs = SlackTimestamp.FromDateTimeOffset(oldest);
        var channel = Uri.EscapeDataString(channelId);
        var messages = await GetAllPagesAsync("conversations.history", $"channel={channel}&oldest={oldestTs}", ct);

        // History only returns thread parents; replies need their own call per thread. Slack limits that call
        // more strictly, so threads with only the bot's feedback or the jury's remarks are skipped: those are no submissions.
        var replies = new List<SlackEventItemDto>();
        foreach (var parent in messages.Where(m => m.ReplyCount > 0 && m.ThreadTs == m.Ts && !OnlyIgnoredRepliers(m)))
        {
            var thread = await GetAllPagesAsync("conversations.replies", $"channel={channel}&ts={Uri.EscapeDataString(parent.Ts!)}", ct);
            replies.AddRange(thread.Where(m => m.Ts != parent.Ts));
        }

        return messages.Concat(replies)
            // A reply also sent to the channel appears in both lists
            .DistinctBy(m => m.Ts)
            .Select(m => { m.Channel = channelId; return m; })
            .OrderBy(m => decimal.Parse(m.Ts!, CultureInfo.InvariantCulture))
            .ToList();
    }

    private bool OnlyIgnoredRepliers(SlackEventItemDto parent) =>
        parent.ReplyUsers is { Count: > 0 } users
        && users.All(u => (_links?.BotUserId != null && u == _links.BotUserId) || (_runtime?.IsReviewer(u) ?? false));

    private async Task<List<SlackEventItemDto>> GetAllPagesAsync(string method, string query, CancellationToken ct)
    {
        var all = new List<SlackEventItemDto>();
        string? cursor = null;
        do
        {
            var url = $"https://slack.com/api/{method}?{query}&limit={PageSize}" + (cursor == null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            var page = await GetPageAsync(method, url, ct);
            all.AddRange(page.Messages ?? new List<SlackEventItemDto>());
            cursor = page.ResponseMetadata?.NextCursor;
        }
        while (!string.IsNullOrEmpty(cursor));

        return all;
    }

    /// <summary>
    /// Reads one page. Reading is safe to repeat, so a rate limit, a network error, a server error or a timeout is
    /// tried again a few times; after that it throws, and the caller skips this channel.
    /// </summary>
    private async Task<SlackHistoryResponseDto> GetPageAsync(string method, string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan wait;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(RequestTimeout);
                using var response = await SendAsync(request, timeout.Token, ct);

                if (attempt < MaxAttempts && (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500))
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt);
                    wait = retryAfter > MaxRetryAfter ? MaxRetryAfter : retryAfter;
                    _logger.LogInformation("Slack answered {Method} with HTTP {Status}, waiting {Wait}", method, (int)response.StatusCode, wait);
                }
                else
                {
                    response.EnsureSuccessStatusCode();
                    var page = await response.Content.ReadFromJsonAsync<SlackHistoryResponseDto>(timeout.Token);
                    if (page is not { Ok: true })
                    {
                        throw new SlackApiException(method, page?.Error ?? "unknown");
                    }

                    return page;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException && attempt < MaxAttempts)
            {
                wait = TimeSpan.FromSeconds(attempt);
                _logger.LogInformation(ex, "Reading {Method} failed (attempt {Attempt}), retrying", method, attempt);
            }

            await _wait(wait, ct);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken timeout, CancellationToken ct)
    {
        try
        {
            return await _httpClient.SendAsync(request, timeout);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Slack did not answer within {RequestTimeout.TotalSeconds:0} s", ex);
        }
    }
}
