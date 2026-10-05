using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Workflow;

/// <summary>
/// Special assignments are given with a letter ("Opdracht A") and come from the same points file,
/// which may also give each assignment a title and a description. Live mode against a fake Slack API.
/// </summary>
public class SpecialAssignmentIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private const string Channel = "C02BOSUILEN";
    private readonly FakeSlackApi _slack = new();
    private readonly string _pointsFile = Path.Combine(Path.GetTempPath(), $"punten_{Guid.NewGuid():N}.csv");
    private readonly TestApp _app;
    private HttpClient _client = default!;

    public SpecialAssignmentIntegrationTests(WebApplicationFactory<Program> factory)
    {
        File.WriteAllText(_pointsFile,
            "Opdracht;standaard;awesome;titel;beschrijving\n" +
            "1;1;2;Kampvuur;Maak een vuur zonder aansteker\n" +
            "2;2;4\n" +
            "A;3;6;Speciale toren;\"Bouw een toren; minstens 2 meter, met sjorringen\"\n");
        _app = TestApp.Live(factory, _slack, new Dictionary<string, string?> { ["Scouting:AssignmentPointsFile"] = _pointsFile });
    }

    public Task InitializeAsync()
    {
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    private Task<IReadOnlyList<Submission>> Submissions(string ts) =>
        _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetBySlackMessageIdAsync(Channel, ts));

    private async Task<int> BosuilenScoreAsync()
    {
        var stats = await _app.GetAsync(sp => sp.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync());
        return stats.GroupStatistics.Single(g => g.GroupName == "De Bosuilen").Score;
    }

    [Fact]
    public async Task LetterAssignment_ShouldBeStoredNamedAndScored()
    {
        var scoreBefore = await BosuilenScoreAsync();
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "opdracht a: onze toren!", SlackEvents.File("toren.jpg")));

        var submission = (await Submissions(ts)).Should().ContainSingle().Subject;
        submission.AssignmentCode.Should().Be("A");
        Path.GetFileName(submission.StoragePath).Should().StartWith("DBU A ");
        File.Exists(submission.StoragePath).Should().BeTrue();

        await _app.SendEventAsync(_client, SlackEvents.Reaction(Channel, ts, "thumbsup"));

        (await Submissions(ts)).Single().Status.Should().Be(SubmissionStatus.Approved);
        (await BosuilenScoreAsync()).Should().Be(scoreBefore + 3);
    }

    [Fact]
    public async Task UnknownLetter_ShouldAskForTheNumber()
    {
        var ts = SlackEvents.Ts();

        await _app.SendEventAsync(_client, SlackEvents.Message(Channel, ts, "Opdracht is klaar", SlackEvents.File("klaar.jpg")));

        (await Submissions(ts)).Should().BeEmpty();
        _slack.ThreadReplies(ts).Should().ContainSingle().Which["text"].Should().StartWith("Er staat geen opdrachtnummer");
    }

    [Fact]
    public async Task AssignmentPages_ShouldShowTitleAndDescription_AndSortLettersLast()
    {
        using var browser = await _app.CreateLoggedInClientAsync();

        var detail = await browser.GetStringAsync("/assignments/a");
        detail.Should().Contain("Opdracht A").And.Contain("Speciale toren").And.Contain("Bouw een toren; minstens 2 meter, met sjorringen");

        var assignments = await _app.GetAsync(sp => sp.GetRequiredService<IAssignmentRepository>().GetAllAsync());
        assignments.Select(a => a.Code).Should().Equal("1", "2", "A");
        assignments[0].Title.Should().Be("Kampvuur");
        assignments[1].Title.Should().BeNull();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        File.Delete(_pointsFile);
    }
}
