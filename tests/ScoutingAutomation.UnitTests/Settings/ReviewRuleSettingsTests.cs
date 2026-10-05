using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>Blocking attempts after approval and the reminder time: from appsettings, changeable in the app.</summary>
public class ReviewRuleSettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RuntimeSettingsService Create(bool block = false, int? minutes = null, string? channel = "C_JURY") =>
        new(Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            BlockAttemptsAfterApproval = block,
            ReviewReminder = new ReviewReminderConfig { AfterMinutes = minutes, ChannelId = channel }
        }), NullLogger<RuntimeSettingsService>.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Block_ShouldStartFromAppSettings(bool configured)
    {
        var runtime = Create(block: configured);

        runtime.BlockAttemptsAfterApproval.Should().Be(configured);
        runtime.IsBlockAttemptsChanged.Should().BeFalse();
    }

    [Fact]
    public void Block_SwitchedInTheApp_ShouldSurviveARestartUntilReset()
    {
        var changes = 0;
        var runtime = Create(block: true);
        runtime.Changed += () => changes++;

        runtime.SetBlockAttemptsAfterApproval(false);
        runtime.SetBlockAttemptsAfterApproval(false);

        changes.Should().Be(1);
        Create(block: true).BlockAttemptsAfterApproval.Should().BeFalse();

        runtime.ResetBlockAttemptsAfterApproval();
        Create(block: true).BlockAttemptsAfterApproval.Should().BeTrue();
        runtime.IsBlockAttemptsChanged.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(15, 15)]
    public void Minutes_ShouldStartFromAppSettings(int? configured, int? expected) =>
        Create(minutes: configured).ReviewReminderMinutes.Should().Be(expected);

    [Fact]
    public void Minutes_SetInTheApp_ShouldSurviveARestartUntilReset()
    {
        var runtime = Create(minutes: 15);

        runtime.SetReviewReminderMinutes(5).Should().BeNull();
        Create(minutes: 15).ReviewReminderMinutes.Should().Be(5);

        // Switched off in the app: appsettings no longer applies
        runtime.SetReviewReminderMinutes(null).Should().BeNull();
        Create(minutes: 15).ReviewReminderMinutes.Should().BeNull();
        runtime.IsReviewReminderChanged.Should().BeTrue();

        runtime.ResetReviewReminder();
        Create(minutes: 15).ReviewReminderMinutes.Should().Be(15);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(RuntimeSettingsService.MaxReminderMinutes + 1)]
    public void Minutes_OutOfRange_ShouldBeRefused(int minutes)
    {
        var runtime = Create(minutes: 15);

        runtime.SetReviewReminderMinutes(minutes).Should().NotBeNull();
        runtime.ReviewReminderMinutes.Should().Be(15);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData(" C_JURY ", "C_JURY")]
    public void Channel_ShouldComeFromAppSettings(string? configured, string? expected) =>
        Create(channel: configured).ReminderChannelId.Should().Be(expected);

    [Fact]
    public void AlreadyCompletedText_ShouldBeEditableLikeTheOtherFeedback()
    {
        var runtime = Create();

        runtime.Feedback.AlreadyCompleted.Reaction.Should().Be("lock");
        runtime.SetText(SlackText.AlreadyCompleted, "Al klaar: {number}");

        runtime.Feedback.AlreadyCompleted.Message.Should().Be("Al klaar: {number}");
        Create().Text(SlackText.AlreadyCompleted).Should().Be("Al klaar: {number}");
    }
}
