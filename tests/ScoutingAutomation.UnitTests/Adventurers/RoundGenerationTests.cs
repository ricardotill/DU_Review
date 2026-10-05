using FluentAssertions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

public class RoundGenerationTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static readonly DateTime Day = new(2026, 10, 3);

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Generate_NoonUntilHalfPastFour_ShouldGiveNineRounds()
    {
        var rounds = RuntimeSettingsService.GenerateRounds(Day.AddHours(12), Day.AddHours(16.5), 30);

        rounds.Should().HaveCount(9);
        rounds[0].Start.Should().Be(Day.AddHours(12));
        rounds[0].End.Should().Be(Day.AddHours(12.5));
        rounds[^1].Start.Should().Be(Day.AddHours(16));
        rounds[^1].End.Should().Be(Day.AddHours(16.5));
        rounds.Select(r => r.Number).Should().Equal(Enumerable.Range(1, 9));
        rounds.Select(r => r.Themes.Single()).Should().Equal(Enumerable.Range(1, 9).Select(n => n.ToString()));
    }

    [Fact]
    public void Generate_ShouldOnlyMakeWholeRounds()
    {
        RuntimeSettingsService.GenerateRounds(Day.AddHours(12), Day.AddHours(12).AddMinutes(50), 30).Should().HaveCount(1);
        RuntimeSettingsService.GenerateRounds(Day.AddHours(12), Day.AddHours(12).AddMinutes(20), 30).Should().BeEmpty();
    }

    [Fact]
    public void Check_OverlappingRounds_ShouldBeRefused()
    {
        var rounds = new List<ThemeRoundConfig>
        {
            new() { Number = 1, Start = Day.AddHours(12), End = Day.AddHours(12.5), Themes = { "1" } },
            new() { Number = 2, Start = Day.AddHours(12.25), End = Day.AddHours(13), Themes = { "2" } }
        };

        RuntimeSettingsService.CheckRounds(rounds, 30).Should().NotBeNull();
    }

    [Fact]
    public void Check_ThemeInTwoRounds_ShouldBeRefused()
    {
        var rounds = new List<ThemeRoundConfig>
        {
            new() { Number = 1, Start = Day.AddHours(12), Themes = { "1" } },
            new() { Number = 2, Start = Day.AddHours(13), Themes = { "1" } }
        };

        RuntimeSettingsService.CheckRounds(rounds, 30).Should().Contain("1");
    }

    [Fact]
    public void Check_DuplicateNumber_ShouldBeRefused()
    {
        var rounds = new List<ThemeRoundConfig>
        {
            new() { Number = 1, Start = Day.AddHours(12), Themes = { "1" } },
            new() { Number = 1, Start = Day.AddHours(13), Themes = { "2" } }
        };

        RuntimeSettingsService.CheckRounds(rounds, 30).Should().Contain("twee keer");
    }

    [Fact]
    public void SetRounds_ShouldWinOverAppsettingsAndSurviveARestart()
    {
        var settings = new ScoutingSettings { RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json") };
        settings.Adventurers.Rounds.Add(new ThemeRoundConfig { Number = 1, Start = Day.AddHours(10), Themes = { "1" } });
        var runtime = TestRuntime.For(Options.Create(settings));

        runtime.SetRounds(RuntimeSettingsService.GenerateRounds(Day.AddHours(12), Day.AddHours(13), 30)).Should().BeNull();

        var restarted = TestRuntime.For(Options.Create(settings));
        restarted.ThemeRounds.Should().HaveCount(2);
        restarted.ThemeRounds[0].Start.Should().Be(GameTime.FromGameTime(Day.AddHours(12)));
        restarted.IsRoundsChanged.Should().BeTrue();

        restarted.ResetRounds();
        restarted.ThemeRounds.Single().Start.Should().Be(GameTime.FromGameTime(Day.AddHours(10)));
    }

    [Fact]
    public void RoundWithoutEnd_ShouldLastTheRoundLength()
    {
        var settings = new ScoutingSettings { RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json") };
        settings.Adventurers.RoundMinutes = 20;
        settings.Adventurers.Rounds.Add(new ThemeRoundConfig { Number = 1, Start = Day.AddHours(12), Themes = { "1" } });

        var round = TestRuntime.For(Options.Create(settings)).ThemeRounds.Single();

        (round.End - round.Start).Should().Be(TimeSpan.FromMinutes(20));
    }
}
