using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>Hiding the standing from groups: by hand, in periods, on the beamer, and the interim standing.</summary>
public class StandingSettingsTests : IDisposable
{
    private static readonly DateTime From = new(2026, 10, 3, 15, 0, 0);
    private static readonly DateTime To = new(2026, 10, 3, 17, 0, 0);

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private RuntimeSettingsService Create() =>
        new(Options.Create(new ScoutingSettings { RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json") }),
            NullLogger<RuntimeSettingsService>.Instance);

    [Fact]
    public void ByDefault_ShouldShowTheStanding()
    {
        var runtime = Create();

        runtime.IsStandingHidden(DateTimeOffset.UtcNow).Should().BeFalse();
        runtime.StandingHideWindows.Should().BeEmpty();
        runtime.HideStandingOnBeamer.Should().BeFalse();
        runtime.InterimStanding.Should().BeNull();
    }

    [Fact]
    public void Switch_ShouldHideAtAnyTime()
    {
        var runtime = Create();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.SetStandingHidden(true);

        runtime.IsStandingHidden(DateTimeOffset.UtcNow).Should().BeTrue();
        runtime.IsStandingHidden(DateTimeOffset.UtcNow.AddYears(5)).Should().BeTrue();
        changes.Should().Be(1);
    }

    [Fact]
    public void Window_ShouldHideFromItsStartUntilJustBeforeItsEnd()
    {
        var runtime = Create();

        runtime.AddStandingHideWindow(From, To).Should().BeNull();

        var start = GameTime.FromGameTime(From);
        var end = GameTime.FromGameTime(To);
        runtime.IsStandingHidden(start.AddTicks(-1)).Should().BeFalse();
        runtime.IsStandingHidden(start).Should().BeTrue();
        runtime.IsStandingHidden(end.AddTicks(-1)).Should().BeTrue();
        runtime.IsStandingHidden(end).Should().BeFalse();
    }

    [Fact]
    public void NextStandingChange_ShouldBeTheNextStartOrEnd()
    {
        var runtime = Create();
        runtime.AddStandingHideWindow(From, To);
        var start = GameTime.FromGameTime(From);
        var end = GameTime.FromGameTime(To);

        runtime.NextStandingChange(start.AddHours(-1)).Should().Be(start);
        runtime.NextStandingChange(start).Should().Be(end);
        runtime.NextStandingChange(end).Should().BeNull();
    }

    [Fact]
    public void AddWindow_ShouldRefuseAnEndBeforeTheStartAndUnusableTimes()
    {
        var runtime = Create();

        runtime.AddStandingHideWindow(To, From).Should().NotBeNull();
        runtime.AddStandingHideWindow(From, From).Should().NotBeNull();
        runtime.AddStandingHideWindow(new DateTime(1, 1, 1), To).Should().NotBeNull();
        runtime.StandingHideWindows.Should().BeEmpty();
    }

    [Fact]
    public void RemoveWindow_ShouldShowTheStandingAgain()
    {
        var runtime = Create();
        runtime.AddStandingHideWindow(From, To);
        var window = runtime.StandingHideWindows.Single();

        runtime.RemoveStandingHideWindow(window.Id).Should().BeTrue();

        runtime.IsStandingHidden(GameTime.FromGameTime(From)).Should().BeFalse();
        runtime.RemoveStandingHideWindow(window.Id).Should().BeFalse();
    }

    [Fact]
    public void Everything_ShouldSurviveARestart()
    {
        var runtime = Create();
        runtime.SetStandingHidden(true);
        runtime.SetHideStandingOnBeamer(true);
        runtime.AddStandingHideWindow(From, To);
        var groupId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 3, 14, 30, 0, TimeSpan.Zero);
        runtime.PublishInterimStanding([new InterimStandingRow(groupId, "De Wolven", 1, 42)], at);

        var restarted = Create();

        restarted.StandingHiddenManually.Should().BeTrue();
        restarted.HideStandingOnBeamer.Should().BeTrue();
        restarted.StandingHideWindows.Should().ContainSingle(w => w.From == From && w.To == To);
        restarted.InterimStanding!.PublishedAt.Should().Be(at);
        restarted.InterimStanding.Rows.Should().Equal(new InterimStandingRow(groupId, "De Wolven", 1, 42));
    }

    [Fact]
    public void InterimStanding_ShouldBeSortedByRankAndRemovable()
    {
        var runtime = Create();
        runtime.PublishInterimStanding(
            [new InterimStandingRow(Guid.NewGuid(), "B", 2, 10), new InterimStandingRow(Guid.NewGuid(), "A", 1, 20)],
            DateTimeOffset.UtcNow);

        runtime.InterimStanding!.Rows.Select(r => r.GroupName).Should().Equal("A", "B");

        runtime.ClearInterimStanding();

        runtime.InterimStanding.Should().BeNull();
        Create().InterimStanding.Should().BeNull();
    }

    [Fact]
    public void OlderSettingsFile_ShouldStillLoad()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "runtime-settings.json"), """{ "Reviewers": ["U_OLD"] }""");

        var runtime = Create();

        runtime.AddedReviewers.Should().Contain("U_OLD");
        runtime.IsStandingHidden(DateTimeOffset.UtcNow).Should().BeFalse();
    }
}
