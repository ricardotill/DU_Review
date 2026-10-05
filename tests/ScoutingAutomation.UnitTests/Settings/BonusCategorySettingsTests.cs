using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Settings;

/// <summary>The point categories entered by hand: from appsettings, changed in the app while the game runs.</summary>
public class BonusCategorySettingsTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private string File => Path.Combine(_temp.Path, "runtime-settings.json");

    private RuntimeSettingsService Create(List<BonusCategoryConfig>? configured = null) =>
        new(Options.Create(new ScoutingSettings { RuntimeSettingsFile = File, BonusCategories = configured }),
            NullLogger<RuntimeSettingsService>.Instance);

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
}
