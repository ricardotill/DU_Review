using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// What the bot tells a group in Slack about its messages, in live mode against a fake Slack API.
/// </summary>
public class SlackFeedbackIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C01ZEEARENDEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public SlackFeedbackIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _slack.MissingFiles.TryAdd("kapot.jpg", 0);
        _slack.UserNames.TryAdd(SlackEvents.Scout, "Pieter Padvinder");
        _app = TestApp.Live(factory, _slack);
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<Submission>> Submissions(string ts, string channel = Channel) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(channel, ts));

    [Theory]
    [InlineData("Opdracht 30", "schema.zip", "application/zip", "warning", "Dit bestandstype wordt niet ondersteund: schema.zip.")]
    [InlineData("Kijk eens!", "foto.jpg", "image/jpeg", "question", "Er staat geen opdrachtnummer in je bericht.")]
    [InlineData("Opdracht 999", "foto.jpg", "image/jpeg", "question", "Opdracht 999 bestaat niet.")]
    [InlineData("Opdracht 31", "kapot.jpg", "image/jpeg", "warning", "Het bestand kapot.jpg kon niet worden opgehaald.")]
    public async Task UnusableMessage_ShouldGetReactionAndThreadReply(string text, string file, string mimetype, string reaction, string reply)
    {
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, text, SlackEvents.File(file, mimetype)));

        (await Submissions(ts)).Should().BeEmpty();
        var added = _slack.Calls("reactions.add").Should().ContainSingle(c => c["timestamp"] == ts).Which;
        added["channel"].Should().Be(Channel);
        added["name"].Should().Be(reaction);
        var thread = _slack.ThreadReplies(ts).Should().ContainSingle().Which;
        thread["channel"].Should().Be(Channel);
        thread["text"].Should().Be(reply);
    }

    [Fact]
    public async Task RedeliveredProblem_ShouldGetFeedbackOnlyOnce()
    {
        var ts = SlackEvents.Ts();
        var message = SlackEvents.Message(Channel, ts, "geen nummer", SlackEvents.File("foto.jpg"));

        await _app.SendEventAsync(_client, message);
        await _app.SendEventAsync(_client, message);

        _slack.Calls("reactions.add").Count(c => c["timestamp"] == ts).Should().Be(2, "the second attempt is how the bot finds out");
        _slack.ThreadReplies(ts).Should().ContainSingle("Slack said the reaction was already there");
    }

    [Fact]
    public async Task RedeliveredUpload_ShouldBeStoredAndDownloadedOnce()
    {
        var ts = SlackEvents.Ts();
        var message = SlackEvents.Message(Channel, ts, "Opdracht 32", SlackEvents.File("dubbel.jpg"));

        var first = await _app.SendEventAsync(_client, message);
        var second = await _app.SendEventAsync(_client, message);

        first.GetProperty("createdSubmissionIds").GetArrayLength().Should().Be(1);
        second.GetProperty("createdSubmissionIds").GetArrayLength().Should().Be(0);
        (await Submissions(ts)).Should().ContainSingle();
        _slack.Calls("file").Count(c => c["name"] == "dubbel.jpg").Should().Be(1);
    }

    [Fact]
    public async Task GoodUpload_ShouldBeStoredWithTheSlackNameAndWithoutFeedback()
    {
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 33", SlackEvents.File("goed.jpg")));

        var submission = (await Submissions(ts)).Single();
        submission.SubmitterName.Should().Be("Pieter Padvinder");
        (await File.ReadAllBytesAsync(submission.StoragePath)).Should().Equal(FakeSlackApi.FileContent);
        _slack.Writes.Should().NotContain(c => c["timestamp"] == ts || c["thread_ts"] == ts);
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync($"/submissions/{submission.Id}")).Should().Contain("Pieter Padvinder");
    }

    [Fact]
    public async Task MixedFiles_ShouldStoreTheGoodOneAndWarnAboutTheRest()
    {
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 34",
            SlackEvents.File("goed.mp4", "video/mp4"), SlackEvents.File("virus.exe", "application/octet-stream")));

        (await Submissions(ts)).Should().ContainSingle().Which.FileName.Should().Be("goed.mp4");
        _slack.ThreadReplies(ts).Should().ContainSingle().Which["text"].Should().Contain("virus.exe").And.NotContain("goed.mp4");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("file_share", true)]
    [InlineData("thread_broadcast", true)]
    [InlineData("bot_message", false)]
    [InlineData("channel_join", false)]
    public async Task MessageSubtypes_ShouldOnlyRegisterMessagesFromPeople(string? subtype, bool registered)
    {
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 35", SlackEvents.Scout, subtype, SlackEvents.File("sub.jpg")));

        (await Submissions(ts)).Should().HaveCount(registered ? 1 : 0);
    }

    [Fact]
    public async Task MessagesFromOtherChannelsOrReviewers_ShouldBeIgnoredSilently()
    {
        var otherTs = SlackEvents.Ts();
        var juryTs = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message("C99ONBEKEND", otherTs, "hallo", SlackEvents.File("x.jpg")));
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, juryTs, "hallo", TestApp.Reviewer, "file_share", SlackEvents.File("x.jpg")));

        (await Submissions(otherTs, "C99ONBEKEND")).Should().BeEmpty();
        (await Submissions(juryTs)).Should().BeEmpty();
        _slack.Writes.Should().NotContain(c => c["timestamp"] == otherTs || c["timestamp"] == juryTs);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
