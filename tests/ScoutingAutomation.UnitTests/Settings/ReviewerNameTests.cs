using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

public class ReviewerNameTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    private string File_ => Path.Combine(_temp.Path, "runtime-settings.json");

    private RuntimeSettingsService Runtime() => new(Options.Create(new ScoutingSettings
    {
        Reviewers = new List<string> { "U_EVA", "U_PIET" },
        ReviewerNames = new Dictionary<string, string> { ["u_eva"] = " Eva uit config ", ["U_PIET"] = " " },
        RuntimeSettingsFile = File_
    }), NullLogger<RuntimeSettingsService>.Instance);

    [Fact]
    public void Names_ShouldComeFromTheAppBeforeAppSettings()
    {
        var runtime = Runtime();

        runtime.NameOf("U_EVA").Should().Be("Eva uit config");
        runtime.NameOf("U_PIET").Should().BeNull("an empty configured name counts as none");
        runtime.DisplayName("U_PIET").Should().Be("U_PIET");
        runtime.DisplayName(null).Should().BeEmpty();
        runtime.NameOf(" ").Should().BeNull();

        runtime.SetReviewerName("U_EVA", " Eva ").Should().BeNull();
        runtime.NameOf("u_eva").Should().Be("Eva");
        runtime.HasManualName("U_EVA").Should().BeTrue();
        Runtime().NameOf("U_EVA").Should().Be("Eva", "names are kept after a restart");
    }

    [Fact]
    public void SetReviewerName_ShouldValidateAndAllowRemoving()
    {
        var runtime = Runtime();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.SetReviewerName("U_ONBEKEND", "X").Should().Contain("geen jurylid");
        runtime.SetReviewerName("U_PIET", new string('x', RuntimeSettingsService.MaxNameLength + 1)).Should().Contain("maximaal");
        runtime.SetReviewerName("U_PIET", "Piet").Should().BeNull();
        runtime.SetReviewerName("U_PIET", "").Should().BeNull();

        runtime.NameOf("U_PIET").Should().BeNull();
        changes.Should().Be(2);
    }

    [Fact]
    public void NameFromSlack_ShouldNeverReplaceANameEnteredByHand()
    {
        var runtime = Runtime();

        runtime.SetNameFromSlack("U_PIET", " Piet Slack ").Should().BeTrue();
        runtime.NameOf("U_PIET").Should().Be("Piet Slack");
        runtime.HasManualName("U_PIET").Should().BeFalse();

        runtime.SetReviewerName("U_PIET", "Piet");
        runtime.SetNameFromSlack("U_PIET", "Iets anders").Should().BeFalse();
        runtime.SetNameFromSlack("U_EVA", " ").Should().BeFalse();
        runtime.NameOf("U_PIET").Should().Be("Piet");
    }

    [Fact]
    public void NameFromSlack_TooLong_ShouldBeShortened()
    {
        var runtime = Runtime();

        runtime.SetNameFromSlack("U_EVA", new string('e', 100));

        runtime.NameOf("U_EVA").Should().HaveLength(RuntimeSettingsService.MaxNameLength);
    }

    [Fact]
    public void RemovingAddedReviewer_ShouldAlsoRemoveTheirName()
    {
        var runtime = Runtime();
        runtime.AddReviewer("U_NIEUW");
        runtime.SetReviewerName("U_NIEUW", "Nieuw");

        runtime.RemoveReviewer("U_NIEUW");

        runtime.NameOf("U_NIEUW").Should().BeNull();
        runtime.AllReviewers.Should().Equal("U_EVA", "U_PIET");
    }

    [Fact]
    public void SavedFile_ShouldSkipEmptyNames()
    {
        File.WriteAllText(File_, """{"Reviewers":[],"ReviewerNames":{"U_EVA":{"Name":"","FromSlack":true},"U_PIET":{"Name":"Piet","FromSlack":false},"U_X":null}}""");

        var runtime = Runtime();

        runtime.NameOf("U_EVA").Should().Be("Eva uit config");
        runtime.NameOf("U_PIET").Should().Be("Piet");
    }

    [Fact]
    public void WithoutConfiguredNames_ShouldHaveNone()
    {
        var runtime = new RuntimeSettingsService(Options.Create(new ScoutingSettings { Reviewers = new List<string> { "U1" }, ReviewerNames = null!, RuntimeSettingsFile = File_ }),
            NullLogger<RuntimeSettingsService>.Instance);

        runtime.NameOf("U1").Should().BeNull();
    }

    public void Dispose() => _temp.Dispose();
}

public class ReviewerNameFetcherTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly RuntimeSettingsService _runtime;

    public ReviewerNameFetcherTests()
    {
        _runtime = new RuntimeSettingsService(Options.Create(new ScoutingSettings
        {
            Reviewers = new List<string> { "U_EVA", "U_PIET", "U_WEG" },
            RuntimeSettingsFile = Path.Combine(_temp.Path, "rt.json")
        }), NullLogger<RuntimeSettingsService>.Instance);
        _slack.IsConnected.Returns(true);
        _slack.GetUserNameAsync("U_EVA", Arg.Any<CancellationToken>()).Returns(SlackUserLookup.Found("Eva"));
        _slack.GetUserNameAsync("U_PIET", Arg.Any<CancellationToken>()).Returns(SlackUserLookup.Found("Piet"));
        _slack.GetUserNameAsync("U_WEG", Arg.Any<CancellationToken>()).Returns(SlackUserLookup.Failed("user_not_found"));
    }

    [Fact]
    public async Task FetchAllAsync_ShouldFillNamesKeepManualOnesAndReportProblems()
    {
        _runtime.SetReviewerName("U_PIET", "Pietje");
        var fetcher = new ReviewerNameFetcher(_slack, _runtime);

        var result = await fetcher.FetchAllAsync();

        result.Updated.Should().Be(1);
        result.KeptManual.Should().Be(1);
        result.Problems.Should().ContainSingle().Which.Should().Contain("Slack kent U_WEG niet");
        _runtime.NameOf("U_EVA").Should().Be("Eva");
        _runtime.NameOf("U_PIET").Should().Be("Pietje");
        await _slack.DidNotReceive().GetUserNameAsync("U_PIET", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("missing_scope", "users:read")]
    [InlineData("ratelimited", "De naam van U_EVA kon niet worden opgehaald: ratelimited")]
    public async Task FetchAsync_Problems_ShouldExplain(string error, string expected)
    {
        _slack.GetUserNameAsync("U_EVA", Arg.Any<CancellationToken>()).Returns(SlackUserLookup.Failed(error));

        var problem = await new ReviewerNameFetcher(_slack, _runtime).FetchAsync("U_EVA");

        problem.Should().Contain(expected);
    }

    [Fact]
    public void CanFetch_ShouldFollowTheSlackConnection()
    {
        new ReviewerNameFetcher(_slack, _runtime).CanFetch.Should().BeTrue();
        _slack.IsConnected.Returns(false);
        new ReviewerNameFetcher(_slack, _runtime).CanFetch.Should().BeFalse();
    }

    public void Dispose() => _temp.Dispose();
}

public class SlackUserLookupTests
{
    private static SlackClient Create(FakeHttpHandler handler, bool simulation = false) =>
        new(new HttpClient(handler),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = simulation, BotToken = "xoxb-test" } }),
            NullLogger<SlackClient>.Instance);

    [Theory]
    [InlineData("""{"ok":true,"user":{"name":"eva","real_name":"Eva R","profile":{"display_name":"Eva","real_name":"Eva Real"}}}""", "Eva")]
    [InlineData("""{"ok":true,"user":{"name":"eva","real_name":"Eva R","profile":{"display_name":"","real_name":"Eva Real"}}}""", "Eva Real")]
    [InlineData("""{"ok":true,"user":{"name":"eva","real_name":"Eva R","profile":{}}}""", "Eva R")]
    [InlineData("""{"ok":true,"user":{"name":"eva"}}""", "eva")]
    public async Task GetUserNameAsync_ShouldPreferTheDisplayName(string response, string expected)
    {
        var handler = FakeHttpHandler.Json(response);

        var lookup = await Create(handler).GetUserNameAsync("U 1");

        lookup.Name.Should().Be(expected);
        lookup.Error.Should().BeNull();
        handler.Requests.Single().Request.RequestUri!.AbsoluteUri.Should().Be("https://slack.com/api/users.info?user=U%201");
        handler.Requests.Single().Request.Headers.Authorization!.ToString().Should().Be("Bearer xoxb-test");
    }

    [Theory]
    [InlineData("""{"ok":false,"error":"user_not_found"}""", HttpStatusCode.OK, "user_not_found")]
    [InlineData("""{"ok":false}""", HttpStatusCode.OK, "unknown")]
    [InlineData("null", HttpStatusCode.OK, "unknown")]
    [InlineData("""{"ok":true,"user":{"profile":{"display_name":" "}}}""", HttpStatusCode.OK, "no_name")]
    [InlineData("""{"ok":true}""", HttpStatusCode.OK, "no_name")]
    [InlineData("", HttpStatusCode.BadGateway, "request_failed")]
    public async Task GetUserNameAsync_Failures_ShouldGiveTheReason(string response, HttpStatusCode status, string error)
    {
        var lookup = await Create(FakeHttpHandler.Json(response, status)).GetUserNameAsync("U1");

        lookup.Name.Should().BeNull();
        lookup.Error.Should().Be(error);
    }

    [Fact]
    public async Task GetUserNameAsync_InSimulation_ShouldNotCallSlack()
    {
        var handler = FakeHttpHandler.Json("{}");
        var client = Create(handler, simulation: true);

        client.IsConnected.Should().BeFalse();
        (await client.GetUserNameAsync("U1")).Error.Should().Be("simulation_mode");
        handler.Requests.Should().BeEmpty();
        Create(handler).IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task GetUserNameAsync_Cancelled_ShouldThrow()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var act = () => Create(FakeHttpHandler.Json("{}")).GetUserNameAsync("U1", cancelled.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
