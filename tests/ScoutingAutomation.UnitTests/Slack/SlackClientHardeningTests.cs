using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

/// <summary>Time limits, rate limits and checks on what Slack sends back.</summary>
public class SlackClientHardeningTests
{
    private readonly List<TimeSpan> _waits = new();

    private SlackClient Create(HttpMessageHandler handler, SlackOwnUploads? ownUploads = null) =>
        new(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb-test" } }),
            NullLogger<SlackClient>.Instance,
            (wait, _) => { _waits.Add(wait); return Task.CompletedTask; },
            ownUploads)
        {
            DownloadIdleTimeout = TimeSpan.FromMilliseconds(200),
            ApiTimeout = TimeSpan.FromMilliseconds(200)
        };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    [Fact]
    public async Task Download_ThatStopsSendingData_ShouldGiveUpInsteadOfHangingTheGroup()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        await using var stream = await Create(handler).DownloadFileAsync("https://files.slack.com/video.mp4", "video.mp4");

        var copy = () => stream.CopyToAsync(Stream.Null);

        await copy.Should().ThrowAsync<TimeoutException>().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Download_OfASlackLoginPage_ShouldFailInsteadOfStoringIt()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Sign in to Slack</html>", Encoding.UTF8, "text/html")
        });

        var act = () => Create(handler).DownloadFileAsync("https://files.slack.com/a.jpg", "a.jpg");

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*files:read*");
    }

    [Theory]
    [InlineData("https://evil.example.com/a.jpg")]
    [InlineData("http://files.slack.com/a.jpg")]
    [InlineData("sim://a.jpg")]
    public async Task Download_FromAnAddressOutsideSlack_ShouldNeverSendTheBotToken(string url)
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        var act = () => Create(handler).DownloadFileAsync(url, "a.jpg");

        await act.Should().ThrowAsync<HttpRequestException>();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Post_WhenRateLimited_ShouldWaitAsSlackAsksAndTryAgain()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => ++calls == 1 ? RateLimited(3) : Json("""{"ok":true}"""));

        var error = await Create(handler).PostMessageAsync("C1", "hallo");

        error.Should().BeNull();
        calls.Should().Be(2, "a rate-limited call was not handled by Slack, so trying again cannot post twice");
        _waits.Should().Equal(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Post_StillRateLimited_ShouldSaySo()
    {
        var handler = new FakeHttpHandler(_ => RateLimited(1));

        var error = await Create(handler).PostMessageAsync("C1", "hallo");

        error.Should().Be(SlackClient.RateLimited);
        handler.Requests.Should().HaveCount(SlackClient.ApiAttempts);
    }

    [Fact]
    public async Task Post_ServerError_ShouldNotBeRetried()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));

        var error = await Create(handler).PostMessageAsync("C1", "hallo");

        error.Should().Be(SlackClient.RequestFailed);
        handler.Requests.Should().ContainSingle("Slack may have posted it before the error");
    }

    [Fact]
    public async Task ReadingReactions_AfterAServerError_ShouldTryAgain()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json("""{"ok":true,"message":{"reactions":[{"name":"heart","users":["U1"]}]}}"""));

        var reactions = await Create(handler).GetReactionsAsync("C1", "1.0");

        reactions.Should().ContainSingle().Which.Name.Should().Be("heart");
    }

    [Fact]
    public async Task Call_ThatSlackNeverAnswers_ShouldTimeOut()
    {
        var error = await Create(new SilentHandler()).PostMessageAsync("C1", "hallo").WaitAsync(TimeSpan.FromSeconds(5));

        error.Should().Be(SlackClient.Timeout);
    }

    [Fact]
    public async Task Call_CancelledByTheApp_ShouldStopInsteadOfReportingAnError()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var act = () => Create(new SilentHandler()).PostMessageAsync("C1", "hallo", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PostFile_ShouldRememberTheUploadSoItIsNeverTakenForASubmission()
    {
        var own = new SlackOwnUploads();
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/files.getUploadURLExternal" => Json("""{"ok":true,"file_id":"F_OWN","upload_url":"https://files.slack.com/upload/v1/x"}"""),
            "/upload/v1/x" => new HttpResponseMessage(HttpStatusCode.OK),
            _ => Json("""{"ok":true}""")
        });

        await Create(handler, own).PostFileAsync("C1", new byte[] { 1 }, "foto.jpg", null);

        own.Contains("F_OWN").Should().BeTrue();
    }

    /// <summary>A body that sends nothing and never ends, like a connection that went silent.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Slack that never answers.</summary>
    private sealed class SilentHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
