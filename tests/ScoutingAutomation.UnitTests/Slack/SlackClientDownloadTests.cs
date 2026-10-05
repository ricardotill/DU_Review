using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Slack;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackClientDownloadTests
{
    private static SlackClient Create(FakeHttpHandler handler, List<TimeSpan>? waits = null) =>
        new(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" } }),
            NullLogger<SlackClient>.Instance,
            (wait, _) => { waits?.Add(wait); return Task.CompletedTask; });

    [Fact]
    public async Task Download_ServerErrorThenSuccess_ShouldRetry()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var handler = new FakeHttpHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 9 }) });

        await using var stream = await Create(handler, waits).DownloadFileAsync("https://files.slack.com/a.mp4", "a.mp4");
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);

        copy.ToArray().Should().Equal(9);
        calls.Should().Be(2);
        waits.Should().Equal(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Download_RateLimited_ShouldWaitAsSlackAsks()
    {
        var calls = 0;
        var waits = new List<TimeSpan>();
        var handler = new FakeHttpHandler(_ =>
        {
            if (++calls > 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1 }) };
            var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
            return limited;
        });

        await using var _ = await Create(handler, waits).DownloadFileAsync("https://files.slack.com/a.jpg", "a.jpg");

        waits.Should().Equal(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Download_KeepsFailing_ShouldGiveUpAfterThreeAttempts()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var act = () => Create(handler).DownloadFileAsync("https://files.slack.com/a.jpg", "a.jpg");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(SlackClient.DownloadAttempts);
    }

    [Fact]
    public async Task Download_NotFound_ShouldNotRetry()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var act = () => Create(handler).DownloadFileAsync("https://files.slack.com/a.jpg", "a.jpg");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task RemoveReaction_ShouldCallSlackWithoutColons()
    {
        var handler = FakeHttpHandler.Json("""{"ok":false,"error":"no_reaction"}""");

        await Create(handler).RemoveReactionAsync("C1", "1.0", ":question:");

        var (request, body) = handler.Requests.Single();
        request.RequestUri!.ToString().Should().Be("https://slack.com/api/reactions.remove");
        body.Should().Contain("\"name\":\"question\"");
    }
}
