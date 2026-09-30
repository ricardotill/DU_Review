using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;

namespace ScoutingAutomation.Infrastructure.Services;

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
    private const int MaxRateLimitRetries = 5;

    private readonly HttpClient _httpClient;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<SlackHistoryClient> _logger;

    public SlackHistoryClient(HttpClient httpClient, IOptions<ScoutingSettings> options, ILogger<SlackHistoryClient> logger)
    {
        _httpClient = httpClient;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SlackEventItemDto>> GetChannelMessagesAsync(string channelId, DateTimeOffset oldest, CancellationToken ct = default)
    {
        var oldestTs = SlackTimestamp.FromDateTimeOffset(oldest);
        var messages = await GetAllPagesAsync("conversations.history", $"channel={channelId}&oldest={oldestTs}", ct);

        // History only returns thread parents; replies need their own call per thread
        var replies = new List<SlackEventItemDto>();
        foreach (var parent in messages.Where(m => m.ReplyCount > 0 && m.ThreadTs == m.Ts))
        {
            var thread = await GetAllPagesAsync("conversations.replies", $"channel={channelId}&ts={parent.Ts}", ct);
            replies.AddRange(thread.Where(m => m.Ts != parent.Ts));
        }

        return messages.Concat(replies)
            // A reply also sent to the channel appears in both lists
            .DistinctBy(m => m.Ts)
            .Select(m => { m.Channel = channelId; return m; })
            .OrderBy(m => decimal.Parse(m.Ts!, CultureInfo.InvariantCulture))
            .ToList();
    }

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

    private async Task<SlackHistoryResponseDto> GetPageAsync(string method, string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Value.Slack.BotToken);
            using var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                _logger.LogInformation("Slack rate limit on {Method}, waiting {Wait}", method, wait);
                await Task.Delay(wait, ct);
                continue;
            }

            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SlackHistoryResponseDto>(ct);
            if (page is not { Ok: true })
            {
                throw new SlackApiException(method, page?.Error ?? "unknown");
            }

            return page;
        }
    }
}
