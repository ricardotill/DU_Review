using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Messages from the jury, posted by the bot in the chosen group channels of a fake Slack API.
/// </summary>
public class GroupMessagesIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSlackApi _slack = new();

    public GroupMessagesIntegrationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static Task<IReadOnlyList<AnnouncementPostResult>> PostAsync(TestApp app, string text, int groupCount, MessagePicture? picture = null) =>
        app.GetAsync(async services =>
        {
            var groups = await services.GetRequiredService<IGroupRepository>().GetAllAsync();
            var chosen = groups.OrderBy(g => g.SlackChannelId).Take(groupCount).Select(g => g.Id).ToList();
            return await services.GetRequiredService<GroupMessageSender>().PostAsync(text, chosen, picture);
        });

    [Fact]
    public async Task Message_ShouldBePostedOnceInEachChosenChannel()
    {
        await using var app = TestApp.Live(_factory, _slack);

        var results = await PostAsync(app, "*Lunch* om 12:00 &amp; daarna verder 🍕", groupCount: 2);

        results.Should().HaveCount(2).And.OnlyContain(r => r.Posted);
        _slack.Posts.Select(p => p["channel"]).Should().BeEquivalentTo("C01ZEEARENDEN", "C02BOSUILEN");
        _slack.Posts.Should().OnlyContain(p => p["text"] == "*Lunch* om 12:00 &amp; daarna verder 🍕");
    }

    [Fact]
    public async Task SlackError_ShouldBeReportedPerChannel()
    {
        _slack.Errors["chat.postMessage"] = "not_in_channel";
        await using var app = TestApp.Live(_factory, _slack);

        var results = await PostAsync(app, "Hoi", groupCount: 1);

        results.Should().ContainSingle().Which.Should().Match<AnnouncementPostResult>(r => !r.Posted && r.Error == "not_in_channel");
    }

    [Fact]
    public async Task Picture_ShouldBeUploadedAndSharedInEachChosenChannelWithTheText()
    {
        await using var app = TestApp.Live(_factory, _slack);
        var picture = new MessagePicture("kampvuur.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2 });

        var results = await PostAsync(app, "*Kijk* 🔥", groupCount: 2, picture);

        results.Should().HaveCount(2).And.OnlyContain(r => r.Posted);
        _slack.Calls("files.getUploadURLExternal").Should().HaveCount(2).And.OnlyContain(c => c["filename"] == "kampvuur.jpg" && c["length"] == "6");
        _slack.Calls("upload").Should().HaveCount(2).And.OnlyContain(c => c["length"] == "6");
        _slack.SharedFiles.Select(c => c["channel_id"]).Should().BeEquivalentTo("C01ZEEARENDEN", "C02BOSUILEN");
        _slack.SharedFiles.Should().OnlyContain(c => c["initial_comment"] == "*Kijk* 🔥" && c["files"].Contains("\"title\":\"kampvuur.jpg\""));
        _slack.Posts.Should().BeEmpty("the text goes with the picture");
        _slack.Calls("upload").Should().OnlyContain(c => c["host"] == "files.slack.com" && c["authorization"] == "",
            "the upload address grants the upload; the bot token stays with Slack's API");
    }

    [Theory]
    [InlineData("http://files.slack.com/upload/v1/F1")]
    [InlineData("https://files.slack.com.example.org/upload/v1/F1")]
    [InlineData("https://example.org/upload/v1/F1")]
    [InlineData("not a url")]
    public async Task UploadAddress_OutsideSlack_ShouldGetNothing(string uploadUrl)
    {
        _slack.UploadUrl = uploadUrl;
        await using var app = TestApp.Live(_factory, _slack);

        var results = await PostAsync(app, "Kijk", groupCount: 1, new MessagePicture("kampvuur.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));

        results.Should().ContainSingle().Which.Error.Should().Be("invalid_upload_url");
        _slack.Calls("upload").Should().BeEmpty();
        _slack.SharedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task BotsOwnPicture_ShouldNeverBecomeASubmission()
    {
        await using var app = TestApp.Live(_factory, _slack);
        using var client = app.CreateClient();

        var result = await app.SendEventAsync(client, new
        {
            type = "message",
            subtype = "file_share",
            channel = "C01ZEEARENDEN",
            user = "U_BOT",
            bot_id = "B_BOT",
            text = "Opdracht 1",
            ts = SlackEvents.Ts(),
            files = new[] { SlackEvents.File("kampvuur.jpg") }
        });

        result.ToString().Should().Contain("bot");
        _slack.Writes.Should().BeEmpty("the bot does not react to its own message");
        (await app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task BotsOwnPicture_WithoutBotId_ShouldNeverBecomeASubmission()
    {
        // A shared file can come back with only the bot's user ID, not bot_id
        _slack.Workspace = ("T0SCOUTS", "https://scouts.slack.com/");
        _slack.BotUserId = "UBOT";
        await using var app = TestApp.Live(_factory, _slack);
        using var client = app.CreateClient();

        var result = await app.SendEventAsync(client, new
        {
            type = "message",
            subtype = "file_share",
            channel = "C01ZEEARENDEN",
            user = "UBOT",
            text = "Opdracht 1",
            ts = SlackEvents.Ts(),
            files = new[] { SlackEvents.File("kampvuur.jpg") }
        });

        result.ToString().Should().Contain("bot");
        _slack.Writes.Should().BeEmpty("the bot does not react to its own message");
        (await app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().SearchSubmissionsAsync(new SubmissionFilter())))
            .Should().BeEmpty();
    }
}
