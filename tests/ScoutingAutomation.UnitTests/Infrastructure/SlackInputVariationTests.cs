using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Contracts.Slack;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Infrastructure;

/// <summary>
/// Payload shapes Slack can send with fields missing, and settings sections left out.
/// </summary>
public class SlackInputVariationTests
{
    private readonly Mock<ISubmissionRepository> _submissions = new();
    private readonly Mock<IGroupRepository> _groups = new();
    private readonly Mock<IAssignmentRepository> _assignments = new();
    private readonly Mock<IAssignmentParser> _parser = new();
    private readonly Mock<ISlackClient> _slack = new();
    private readonly Group _group = new() { Name = "G", Abbreviation = "G", SlackChannelId = "C1" };

    private SubmissionWorkflowService Create(ScoutingSettings settings)
    {
        var options = Options.Create(settings);
        return new(_submissions.Object, _groups.Object, _assignments.Object, _parser.Object, Mock.Of<IFileStorageService>(), _slack.Object,
            Mock.Of<ISubmissionChangeNotifier>(), TestRuntime.For(options), options, NullLogger<SubmissionWorkflowService>.Instance);
    }

    private void GroupAndAssignmentExist()
    {
        _groups.Setup(g => g.GetBySlackChannelIdAsync("C1", It.IsAny<CancellationToken>())).ReturnsAsync(_group);
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(AssignmentParseResult.Succeeded(1));
        _assignments.Setup(a => a.GetByNumberAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(new Assignment { Number = 1 });
    }

    [Fact]
    public async Task Message_WithoutFilesField_ShouldBeIgnored()
    {
        GroupAndAssignmentExist();

        var result = await Create(new ScoutingSettings()).ProcessSlackMessageAsync(new SlackEventItemDto { Channel = "C1", User = "U", Files = null });

        result.Message.Should().Contain("No files");
    }

    [Fact]
    public async Task Message_ParserFailureWithoutReason_ShouldUseDefaultReason()
    {
        GroupAndAssignmentExist();
        _parser.Setup(p => p.Parse(It.IsAny<string>())).Returns(new AssignmentParseResult(false, null));

        var result = await Create(new ScoutingSettings()).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Files = new List<SlackFileItemDto> { new() { Name = "a.jpg" } }
        });

        result.Message.Should().Be("No assignment number found");
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
        _slack.Setup(s => s.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new MemoryStream());

        await Create(settings).ProcessSlackMessageAsync(new SlackEventItemDto
        {
            Channel = "C1", User = "U", Ts = "1.0", Files = new List<SlackFileItemDto> { new() { Name = name, Mimetype = mimetype, Size = 1 } }
        });

        _slack.Verify(s => s.DownloadFileAsync(It.IsAny<string>(), name, It.IsAny<CancellationToken>()), accepted ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task Reaction_WithoutReactionOrItemFields_ShouldBeIgnored()
    {
        var settings = new ScoutingSettings { Reviewers = new List<string> { "U_JURY" } };

        (await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto { User = "U_JURY", Reaction = null })).Success.Should().BeFalse();
        (await Create(settings).ProcessSlackReactionAsync(new SlackEventItemDto { User = "U_JURY", Reaction = "thumbsup", Item = null })).Message.Should().Contain("missing target");
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

        result.Message.Should().Contain("no review mapping");
    }

    [Fact]
    public async Task Dispatcher_ReactionWithoutItem_ShouldStillReachWorkflow()
    {
        var workflow = new Mock<ISubmissionWorkflowService>();
        var dispatcher = new SlackEventDispatcher(workflow.Object, new SlackProcessingGate(), NullLogger<SlackEventDispatcher>.Instance);

        await dispatcher.DispatchAsync(new SlackEventEnvelopeDto { Type = "event_callback", Event = new SlackEventItemDto { Type = "reaction_added" } });

        workflow.Verify(w => w.ProcessSlackReactionAsync(It.IsAny<SlackEventItemDto>(), It.IsAny<CancellationToken>()), Times.Once);
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
    public void FileName_WithOnlyInvalidCharacters_ShouldGetGeneratedName()
    {
        var name = SubmissionFileName.Build(new Group { Name = "x", Abbreviation = "//" }, null, ".jpg");

        name.Should().StartWith("file_").And.EndWith(".jpg");
    }
}
