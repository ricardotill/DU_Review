using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Workflow;

/// <summary>
/// Payload shapes Slack can send with fields missing, and settings sections left out.
/// </summary>
public class SlackInputVariationTests
{
    private readonly ISubmissionRepository _submissions = Substitute.For<ISubmissionRepository>();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly IAssignmentRepository _assignments = Substitute.For<IAssignmentRepository>();
    private readonly IAssignmentParser _parser = Substitute.For<IAssignmentParser>();
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly Group _group = new() { Name = "G", Abbreviation = "G", SlackChannelId = "C1" };

    private SubmissionWorkflowService Create(ScoutingSettings settings)
    {
        var options = Options.Create(settings);
        return new(_submissions, _groups, _assignments, _parser, Substitute.For<IFileStorageService>(), _slack,
            Substitute.For<ISubmissionChangeNotifier>(), TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    private void GroupAndAssignmentExist()
    {
        _groups.GetBySlackChannelIdAsync("C1", Arg.Any<CancellationToken>()).Returns(_group);
        _parser.Parse(Arg.Any<string>()).Returns(AssignmentParseResult.Succeeded("1"));
        _assignments.GetByCodeAsync("1", Arg.Any<CancellationToken>()).Returns(new Assignment { Code = "1" });
    }

    [Fact]
    public async Task Message_WithoutFilesField_ShouldBeIgnored()
    {
        GroupAndAssignmentExist();

        var result = await Create(new ScoutingSettings()).ProcessSlackMessageAsync(new SlackEventItemDto { Channel = "C1", User = "U", Files = null });

        result.Message.Should().Contain("Geen bestanden");
    }

    [Fact]
    public async Task Message_ParserFailureWithoutReason_ShouldUseDefaultReason()
    {
        GroupAndAssignmentExist();
        _parser.Parse(Arg.Any<string>()).Returns(new AssignmentParseResult(false, null));

        var result = await Create(new ScoutingSettings()).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Files = new List<SlackFileItemDto> { new() { Name = "a.jpg" } }
        });

        result.Message.Should().Be("Geen opdrachtnummer gevonden");
    }

    [Theory]
    [InlineData("README", "image/png", true)]       // no extension, accepted by type
    [InlineData("foto.jpg", null, true)]            // listed extension, no type
    [InlineData("data.bin", null, false)]           // unknown extension, no type
    [InlineData("data.bin", "", false)]
    [InlineData("tekening.svg", "image/svg+xml", false)]
    public async Task Message_FileAcceptance_ShouldUseExtensionThenType(string name, string? mimetype, bool accepted)
    {
        GroupAndAssignmentExist();
        var settings = new ScoutingSettings
        {
            SupportedExtensions = new List<string> { ".jpg" },
            SupportedMimeTypePrefixes = new List<string> { "image/" },
            SlackFeedback = new SlackFeedbackConfig { UnsupportedFile = new FeedbackConfig() }
        };
        _slack.DownloadFileAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new MemoryStream());

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = name, Mimetype = mimetype, Size = 1 } }
        });

        await _slack.Received(accepted ? 1 : 0).DownloadFileAsync(Arg.Any<string>(), name, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reaction_WithoutReactionOrItemFields_ShouldBeIgnored()
    {
        var settings = new ScoutingSettings { Reviewers = new List<string> { "U_JURY" } };

        (await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto { User = "U_JURY", Reaction = null })).Success.Should().BeFalse();
        (await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto { User = "U_JURY", Reaction = "thumbsup", Item = null })).Message.Should().Contain("zonder kanaal of tijdstempel");
    }

    [Fact]
    public async Task Reaction_WithEmojiListsLeftOut_ShouldNotMatch()
    {
        var settings = new ScoutingSettings
        {
            Reviewers = new List<string> { "U_JURY" },
            ReviewStatusReactions = new ReviewStatusReactionsConfig { Approved = null!, Rejected = null!, Awesome = null! }
        };

        var result = await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto
        {
            User = "U_JURY", Reaction = "thumbsup", Item = new SlackReactionTargetDto { Channel = "C1", Ts = "1.0" }
        });

        result.Message.Should().Contain("is geen beoordeling");
    }

    [Fact]
    public async Task Dispatcher_ReactionWithoutItem_ShouldStillReachWorkflow()
    {
        var workflow = Substitute.For<ISubmissionWorkflowService>();
        var dispatcher = new SlackEventDispatcher(workflow, new SlackProcessingGate(), new SlackEventLog(NullLogger<SlackEventLog>.Instance), TimeProvider.System, NullLogger<SlackEventDispatcher>.Instance);

        await dispatcher.DispatchAsync(new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "reaction_added" } });

        await workflow.Received(1).ProcessSlackReactionAsync(Arg.Any<SlackEventItemDto>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"envelope_id":"x"}""", "")]
    [InlineData("""{"type":null}""", "")]
    public void SocketMessage_WithoutType_ShouldHaveEmptyType(string json, string type)
    {
        SlackSocketModeMessage.Parse(Encoding.UTF8.GetBytes(json)).Type.Should().Be(type);
    }

    [Fact]
    public async Task SlackError_WithNullErrorCode_ShouldNotThrow()
    {
        var client = new SlackClient(new HttpClient(FakeHttpHandler.Json("""{"ok":false,"error":null}""")),
            Options.Create(new ScoutingSettings { Slack = new SlackSettings { SimulationMode = false, BotToken = "xoxb" } }),
            NullLogger<SlackClient>.Instance);

        (await client.AddReactionAsync("C1", "1.0", "x")).Should().BeTrue();
    }

    [Fact]
    public void FileName_WithOnlyInvalidCharacters_ShouldKeepTheTime()
    {
        var name = SubmissionFileName.Build(new Group { Name = "x", Abbreviation = "//" }, null, ".jpg", DateTimeOffset.UnixEpoch);

        name.Should().Be("00-00-00.jpg");
    }
}
