using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

public class SlackTextTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RuntimeSettingsService Create() =>
        new(Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            GameStartAnnouncement = new GameStartAnnouncementConfig { Message = "Start {group}" },
            SlackFeedback = new SlackFeedbackConfig
            {
                UnknownAssignmentNumber = new FeedbackConfig { Reaction = "question", Message = "Opdracht {number} bestaat niet." }
            }
        }), NullLogger<RuntimeSettingsService>.Instance);

    [Fact]
    public void Text_ShouldComeFromAppSettingsUntilChanged()
    {
        var runtime = Create();

        runtime.Text(SlackText.GameStart).Should().Be("Start {group}");
        runtime.Text(SlackText.UnknownAssignmentNumber).Should().Be("Opdracht {number} bestaat niet.");
        runtime.IsTextChanged(SlackText.GameStart).Should().BeFalse();
    }

    [Fact]
    public void SetText_ShouldBeUsedAndSurviveARestart()
    {
        var runtime = Create();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.SetText(SlackText.UnknownAssignmentNumber, "  Nummer {number} ken ik niet.  ").Should().BeNull();

        changes.Should().Be(1);
        var restarted = Create();
        restarted.Text(SlackText.UnknownAssignmentNumber).Should().Be("Nummer {number} ken ik niet.");
        restarted.IsTextChanged(SlackText.UnknownAssignmentNumber).Should().BeTrue();
        restarted.Text(SlackText.GameStart).Should().Be("Start {group}");
    }

    [Fact]
    public void Feedback_ShouldUseChangedTextsAndKeepTheConfiguredEmoji()
    {
        var runtime = Create();
        runtime.SetText(SlackText.UnknownAssignmentNumber, "Nieuw");

        var feedback = runtime.Feedback;

        feedback.UnknownAssignmentNumber.Should().BeEquivalentTo(new FeedbackConfig { Reaction = "question", Message = "Nieuw" });
        feedback.MissingAssignmentNumber.Reaction.Should().Be("question", "the defaults still apply to unchanged feedback");
    }

    [Fact]
    public void SetText_Empty_ShouldPostNothing()
    {
        var runtime = Create();

        runtime.SetText(SlackText.GameStart, "   ");

        runtime.Text(SlackText.GameStart).Should().BeEmpty();
        runtime.IsTextChanged(SlackText.GameStart).Should().BeTrue();
    }

    [Fact]
    public void SetText_TooLong_ShouldBeRefused()
    {
        var runtime = Create();

        runtime.SetText(SlackText.GameStart, new string('a', RuntimeSettingsService.MaxSlackTextLength + 1)).Should().NotBeNull();

        runtime.Text(SlackText.GameStart).Should().Be("Start {group}");
    }

    [Fact]
    public void ResetText_ShouldGoBackToAppSettings()
    {
        var runtime = Create();
        runtime.SetText(SlackText.GameStart, "Anders");

        runtime.ResetText(SlackText.GameStart);

        runtime.Text(SlackText.GameStart).Should().Be("Start {group}");
        Create().IsTextChanged(SlackText.GameStart).Should().BeFalse();
    }

    [Fact]
    public void Load_ShouldSkipUnknownKindsAndReadOldFiles()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "runtime-settings.json"),
            """{ "Reviewers": [], "SlackTexts": { "Removed": "x", "GameStart": "Uit bestand" } }""");

        Create().Text(SlackText.GameStart).Should().Be("Uit bestand");
    }
}
