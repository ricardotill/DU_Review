using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Infrastructure.Storage;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Slack;

/// <summary>
/// Scouts editing and deleting their messages in Slack, in live mode against a fake Slack API.
/// </summary>
public class MessageEditDeleteIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C04POOLVOSSEN";
    private readonly FakeSlackApi _slack = new();
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public MessageEditDeleteIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = TestApp.Live(factory, _slack);
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts));

    private static string StoredName(int number, string ts, string extension = ".jpg", int? index = null) =>
        $"DPV {number} {SlackTimestamp.ToDateTimeOffset(ts)!.Value.ToGameTime():HH-mm-ss}{(index is { } i ? $" ({i})" : "")}{extension}";

    private string DeletedFolder => Path.Combine(_app.StorageRoot, LocalFileStorageService.DeletedFolderName, "de-poolvossen");

    [Fact]
    public async Task EditAddingTheMissingNumber_ShouldRegisterAndWithdrawTheQuestionMark()
    {
        var ts = SlackEvents.Ts();
        var files = new[] { SlackEvents.File("vlot.jpg") };

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Ons vlot!", files));
        (await Submissions(ts)).Should().BeEmpty();
        _slack.Calls("reactions.add").Should().ContainSingle(c => c["timestamp"] == ts).Which["name"].Should().Be("question");

        var result = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Ons vlot!", "Opdracht 21: ons vlot!", files));

        result.GetProperty("createdSubmissionIds").GetArrayLength().Should().Be(1);
        var submission = (await Submissions(ts)).Single();
        submission.AssignmentCode.Should().Be("21");
        Path.GetFileName(submission.StoragePath).Should().Be(StoredName(21, ts));
        _slack.Calls("reactions.remove").Should().ContainSingle(c => c["timestamp"] == ts && c["name"] == "question",
            "the missing and unknown number share one emoji, removed once");
    }

    [Fact]
    public async Task EditCorrectingTheNumber_ShouldMoveSubmissionAndRenameItsFile()
    {
        var ts = SlackEvents.Ts();
        var files = new[] { SlackEvents.File("kampvuur.jpg") };
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 22", files));
        var before = (await Submissions(ts)).Single();

        var result = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 22", "Opdracht 23", files));

        result.GetProperty("success").GetBoolean().Should().BeTrue(result.ToString());
        var after = (await Submissions(ts)).Single();
        after.Id.Should().Be(before.Id);
        after.AssignmentCode.Should().Be("23");
        Path.GetFileName(after.StoragePath).Should().Be(StoredName(23, ts));
        File.Exists(after.StoragePath).Should().BeTrue();
        File.Exists(before.StoragePath).Should().BeFalse();
    }

    [Fact]
    public async Task EditToAnInvalidNumber_ShouldKeepTheRegisteredSubmission()
    {
        var ts = SlackEvents.Ts();
        var files = new[] { SlackEvents.File("knoop.jpg") };
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 24", files));

        var unknown = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 24", "Opdracht 999", files));
        var none = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 999", "knoop", files));

        unknown.GetProperty("success").GetBoolean().Should().BeFalse();
        none.GetProperty("success").GetBoolean().Should().BeFalse();
        var submission = (await Submissions(ts)).Single();
        submission.AssignmentCode.Should().Be("24");
        File.Exists(submission.StoragePath).Should().BeTrue();
        _slack.Writes.Should().NotContain(c => c["timestamp"] == ts || c["thread_ts"] == ts, "a typo in an edit is not worth a message");
    }

    [Fact]
    public async Task EditRemovingOneFile_ShouldOnlyDropThatFile()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 25",
            SlackEvents.File("een.jpg", id: "F_EEN"), SlackEvents.File("twee.jpg", id: "F_TWEE")));
        var both = await Submissions(ts);
        both.Select(s => Path.GetFileName(s.StoragePath)).Should().BeEquivalentTo(StoredName(25, ts, index: 1), StoredName(25, ts, index: 2));

        await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 25", "Opdracht 25",
            new[] { SlackEvents.File("een.jpg", id: "F_EEN"), SlackEvents.DeletedFile("F_TWEE") }));

        var left = (await Submissions(ts)).Single();
        left.SlackFileId.Should().Be("F_EEN");
        File.Exists(left.StoragePath).Should().BeTrue();
        File.Exists(Path.Combine(DeletedFolder, StoredName(25, ts, index: 2))).Should().BeTrue("deleted files are kept aside, not thrown away");
        File.Exists(both.Single(s => s.SlackFileId == "F_TWEE").StoragePath).Should().BeFalse();
    }

    [Fact]
    public async Task DeletedMessage_ShouldNoLongerCountButKeepItsFile()
    {
        var ts = SlackEvents.Ts();
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 26", SlackEvents.File("tent.jpg")));
        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "thumbsup"));
        var stored = Path.GetFileName((await Submissions(ts)).Single().StoragePath);
        using var browser = await _app.CreateLoggedInClientAsync();
        (await browser.GetStringAsync("/assignments/26")).Should().Contain(stored);

        var result = await _app.SendEventAsync(_client, SlackEvents.Deleted(Channel, ts, "Opdracht 26"));

        result.GetProperty("success").GetBoolean().Should().BeTrue();
        (await Submissions(ts)).Should().BeEmpty();
        File.Exists(Path.Combine(DeletedFolder, stored)).Should().BeTrue();
        (await browser.GetStringAsync("/assignments/26")).Should().NotContain(stored);
        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        stats.AssignmentStatistics.Where(a => a.AssignmentCode == "26").Sum(a => a.ApprovedCount).Should().Be(0);
    }

    [Fact]
    public async Task DeletingAnUnknownMessage_ShouldBeIgnored()
    {
        var result = await _app.SendEventAsync(_client, SlackEvents.Deleted(Channel, SlackEvents.Ts(), "hallo"));

        result.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task BotEditsAndEditsWithoutNewText_ShouldBeIgnored()
    {
        var ts = SlackEvents.Ts();
        var files = new[] { SlackEvents.File("bot.jpg") };
        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht 27", files));

        var bot = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 27", "Opdracht 28", files, botId: "B123"));
        var sameText = await _app.SendEventAsync(_client, SlackEvents.Edited(Channel, ts, "Opdracht 27", "Opdracht 27", files));

        bot.GetProperty("message").GetString().Should().Contain("bot");
        sameText.GetProperty("message").GetString().Should().Contain("niet veranderd");
        (await Submissions(ts)).Single().AssignmentCode.Should().Be("27");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
