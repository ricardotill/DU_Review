using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>The point categories entered by hand: from appsettings, changed in the app while the game runs.</summary>
public class BonusCategorySettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File => Path.Combine(_temp.Path, "runtime-settings.json");

    private RuntimeSettingsService Create(List<BonusCategoryConfig>? configured = null) =>
        TestRuntime.For(Options.Create(new ScoutingSettings { RuntimeSettingsFile = File, BonusCategories = configured }));

    private static IEnumerable<string> Keys(RuntimeSettingsService runtime) => runtime.BonusCategories.Select(c => c.Key);

    [Fact]
    public void WithoutSection_ShouldUseTheCategoriesOfTheFirstGame()
    {
        var runtime = Create();

        Keys(runtime).Should().Equal("avonturiers", "reisbureau");
        runtime.BonusCategories[1].Name.Should().Be("Reisbureau (pubquiz)");
        runtime.IsBonusCategoriesChanged.Should().BeFalse();
    }

    [Fact]
    public void FromAppsettings_ShouldBeUsedAsGiven()
    {
        var runtime = Create(new List<BonusCategoryConfig> { new() { Key = "quiz", Name = "Quiz" } });

        Keys(runtime).Should().Equal("quiz");
        runtime.BonusCategories[0].DisplayShortName.Should().Be("Quiz", "without a short name the name is used");
    }

    [Fact]
    public void AddRenameRemove_ShouldSurviveARestartAndRaiseChanged()
    {
        var runtime = Create();
        var changes = 0;
        runtime.Changed += () => changes++;

        runtime.AddBonusCategory("Foto Wedstrijd", " Fotowedstrijd ", "Fw").Should().BeNull();
        runtime.RenameBonusCategory("foto-wedstrijd", "Fotowedstrijd (zaterdag)", null).Should().BeNull();
        runtime.RemoveBonusCategory("avonturiers").Should().BeTrue();

        changes.Should().Be(3);
        var restarted = Create();
        Keys(restarted).Should().Equal("reisbureau", "foto-wedstrijd");
        restarted.BonusCategories[1].Name.Should().Be("Fotowedstrijd (zaterdag)");
        restarted.BonusCategories[1].ShortName.Should().BeNull();
        restarted.IsBonusCategoriesChanged.Should().BeTrue();
    }

    [Fact]
    public void RemovingAll_ShouldBeKeptAsNoCategories()
    {
        var runtime = Create();

        runtime.RemoveBonusCategory("avonturiers");
        runtime.RemoveBonusCategory("reisbureau");

        Create().BonusCategories.Should().BeEmpty("an empty list chosen in the app is not the same as no choice");
    }

    [Fact]
    public void Reset_ShouldGoBackToAppsettings()
    {
        var runtime = Create();
        runtime.RemoveBonusCategory("reisbureau");

        runtime.ResetBonusCategories();

        Keys(runtime).Should().Equal("avonturiers", "reisbureau");
        Keys(Create()).Should().Equal("avonturiers", "reisbureau");
    }

    [Theory]
    [InlineData("avonturiers", "Nog een keer", "al in gebruik")]
    [InlineData("quiz!", "Quiz", "kleine letters")]
    [InlineData("", "Quiz", "kleine letters")]
    [InlineData("quiz", "  ", "naam")]
    [InlineData("een-hele-lange-sleutel-te-lang", "Quiz", "hoogstens")]
    public void Add_Invalid_ShouldExplainAndChangeNothing(string key, string name, string error)
    {
        var runtime = Create();

        runtime.AddBonusCategory(key, name).Should().Contain(error);

        Keys(runtime).Should().Equal("avonturiers", "reisbureau");
        System.IO.File.Exists(File).Should().BeFalse();
    }

    [Fact]
    public void RemoveUnknown_ShouldReturnFalse()
    {
        Create().RemoveBonusCategory("bestaat-niet").Should().BeFalse();
    }

    [Fact]
    public void HandEditedFileWithABadCategory_ShouldDropOnlyThatOne()
    {
        System.IO.File.WriteAllText(File, """
            { "BonusCategories": [ { "Key": "quiz", "Name": "Quiz" }, { "Key": "Fout Key", "Name": "Fout" }, { "Key": "quiz", "Name": "Dubbel" } ] }
            """);

        var runtime = Create();

        runtime.BonusCategories.Should().ContainSingle().Which.Name.Should().Be("Quiz");
    }

    private RuntimeSettingsService WithAdventurers(List<BonusCategoryConfig>? configured) =>
        TestRuntime.For(Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = File,
            BonusCategories = configured,
            Adventurers = new AdventurersSettings { Enabled = true }
        }));

    [Fact]
    public void AdventurersOn_ShouldAlwaysHaveTheirCategoryFirst()
    {
        var runtime = WithAdventurers(new List<BonusCategoryConfig> { new() { Key = "quiz", Name = "Quiz" } });

        Keys(runtime).Should().Equal("avonturiers", "quiz");
        runtime.IsFixedBonusCategory("avonturiers").Should().BeTrue();
        runtime.IsFixedBonusCategory("quiz").Should().BeFalse();
    }

    [Fact]
    public void AdventurersOn_TheirCategory_ShouldNotBeRemovedButCanBeRenamed()
    {
        var runtime = WithAdventurers(new List<BonusCategoryConfig> { new() { Key = "quiz", Name = "Quiz" } });

        runtime.RemoveBonusCategory("avonturiers").Should().BeFalse();
        runtime.RenameBonusCategory("avonturiers", "Lopers", "Lp").Should().BeNull();
        runtime.RemoveBonusCategory("quiz").Should().BeTrue();

        Keys(runtime).Should().Equal("avonturiers");
        runtime.BonusCategories[0].Name.Should().Be("Lopers");
    }

    [Fact]
    public void AdventurersOff_TheirCategory_ShouldBeAnOrdinaryOne()
    {
        var runtime = Create();

        runtime.IsFixedBonusCategory("avonturiers").Should().BeFalse();
        runtime.RemoveBonusCategory("avonturiers").Should().BeTrue();
        Keys(runtime).Should().Equal("reisbureau");
    }

    [Fact]
    public void MaxPoints_SetInTheApp_ShouldWinSurviveARestartAndReset()
    {
        var runtime = WithAdventurers(null);
        runtime.ThemeMaxPoints.Should().Be(14);

        runtime.SetMaxPoints(20, 10).Should().BeNull();
        runtime.SetMaxPoints(-1, 10).Should().NotBeNull();
        runtime.SetMaxPoints(20, 1001).Should().NotBeNull();

        var restarted = WithAdventurers(null);
        (restarted.ThemeMaxPoints, restarted.FinalMaxPoints, restarted.IsMaxPointsChanged).Should().Be((20, 10, true));

        restarted.ResetMaxPoints();
        (restarted.ThemeMaxPoints, restarted.FinalMaxPoints, restarted.IsMaxPointsChanged).Should().Be((14, 14, false));
    }

    [Fact]
    public void SuperLocations_SetInTheApp_ShouldWinSurviveARestartAndReset()
    {
        var runtime = WithAdventurers(null);
        runtime.SuperLocations.Should().BeEmpty("they are off by default");

        runtime.SetSuperLocations(true, new[] { 20, 10, 10 }).Should().BeNull();
        runtime.SetSuperLocations(true, new[] { 0 }).Should().NotBeNull();
        runtime.SuperLocations.Should().BeEquivalentTo(new[] { 10, 20 });

        var restarted = WithAdventurers(null);
        restarted.SuperLocationsSetting.Numbers.Should().Equal(10, 20);
        restarted.IsSuperLocationsChanged.Should().BeTrue();

        restarted.SetSuperLocations(false, new[] { 10, 20 }).Should().BeNull();
        restarted.SuperLocations.Should().BeEmpty("off keeps the numbers, but none counts double");
        restarted.SuperLocationsSetting.Numbers.Should().Equal(10, 20);

        restarted.ResetSuperLocations();
        restarted.IsSuperLocationsChanged.Should().BeFalse();
        restarted.SuperLocations.Should().BeEmpty();
    }

    [Fact]
    public void SuperLocations_TooMany_ShouldBeRefused()
    {
        var runtime = WithAdventurers(null);

        runtime.SetSuperLocations(true, Enumerable.Range(1, RuntimeSettingsService.MaxSuperLocations + 1)).Should().Contain("hoogstens");
        runtime.SetSuperLocations(true, Enumerable.Range(1, RuntimeSettingsService.MaxSuperLocations)).Should().BeNull();
    }

    [Fact]
    public void SuperLocations_BrokenFile_ShouldLoadCleaned()
    {
        System.IO.File.WriteAllText(File, """{ "Adventurers": { "SuperLocations": { "Enabled": true, "Numbers": [ 20, -1, 0, 20, 10, 99999 ] } } }""");

        var runtime = WithAdventurers(null);

        runtime.SuperLocationsSetting.Numbers.Should().Equal(10, 20);
        runtime.SuperLocations.Should().BeEquivalentTo(new[] { 10, 20 });
    }

    [Fact]
    public void SuperLocations_NumbersMissingInTheFile_ShouldLoadAsNone()
    {
        System.IO.File.WriteAllText(File, """{ "Adventurers": { "SuperLocations": { "Enabled": true, "Numbers": null } } }""");

        var runtime = WithAdventurers(null);

        runtime.SuperLocations.Should().BeEmpty();
        runtime.IsSuperLocationsChanged.Should().BeTrue();
    }

    [Fact]
    public void SuperLocations_FromAppsettings_ShouldBeUsedUntilChangedInTheApp()
    {
        var runtime = new RuntimeSettingsService(Options.Create(new ScoutingSettings
        {
            RuntimeSettingsFile = File,
            Adventurers = new AdventurersSettings { Enabled = true, SuperLocations = new SuperLocationsConfig { Enabled = true, Numbers = new() { 30, 10 } } }
        }), NullLogger<RuntimeSettingsService>.Instance);

        runtime.SuperLocations.Should().BeEquivalentTo(new[] { 10, 30 });
        runtime.SetSuperLocations(false, new[] { 10 }).Should().BeNull();
        runtime.SuperLocations.Should().BeEmpty();
        runtime.ResetSuperLocations();
        runtime.SuperLocations.Should().BeEquivalentTo(new[] { 10, 30 });
    }
}
