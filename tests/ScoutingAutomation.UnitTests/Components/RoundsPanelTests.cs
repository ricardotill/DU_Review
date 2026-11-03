using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Adventurers;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>The grid of theme rounds on /avonturiers/rondes.</summary>
public class RoundsPanelTests : BunitContext
{
    private static readonly DateTime Day = new(2026, 10, 3);
    private readonly TempDirectory _temp = new();
    private readonly RuntimeSettingsService _runtime;
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public RoundsPanelTests()
    {
        var settings = new ScoutingSettings
        {
            RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json"),
            EventStart = Day.AddHours(12),
            EventEnd = Day.AddHours(16.5),
            Adventurers = new AdventurersSettings { Enabled = true }
        };
        var options = Options.Create(settings);
        _runtime = new RuntimeSettingsService(options, NullLogger<RuntimeSettingsService>.Instance);
        Services.AddSingleton(_runtime);
        Services.AddSingleton(options);
        _auth = AddAuthorization();
        _auth.SetAuthorized("Admin");
        _auth.SetRoles(ScoutingAutomation.Web.Auth.AppAuth.AdminRole, ScoutingAutomation.Web.Auth.AppAuth.JuryRole);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _temp.Dispose();
    }

    [Fact]
    public void Generate_ShouldFillTheGridFromTheGameTimes_AndSaveIt()
    {
        var cut = Render<RoundsPanel>();
        cut.Find(".no-rounds");

        cut.Find("button.generate").Click();

        cut.FindAll("tr.round-row").Should().HaveCount(9);
        _runtime.ThemeRounds.Should().BeEmpty("nothing is saved before the admin clicks save");

        cut.Find("button.save-rounds").Click();

        _runtime.ThemeRounds.Should().HaveCount(9);
        _runtime.ThemeRounds[0].Start.Should().Be(GameTime.FromGameTime(Day.AddHours(12)));
        _runtime.ThemeRounds[^1].End.Should().Be(GameTime.FromGameTime(Day.AddHours(16.5)));
        cut.Find(".rounds-saved");
    }

    [Fact]
    public void EditThemes_ShouldSaveSeveralThemesInOneRound()
    {
        var cut = Render<RoundsPanel>();
        cut.Find("button.add-round").Click();

        cut.Find("input.round-themes").Change("T3, 4");
        cut.Find("button.save-rounds").Click();

        _runtime.ThemeRounds.Single().Themes.Should().Equal("3", "4");
    }

    [Fact]
    public void OverlappingRounds_ShouldShowWhyAndKeepWhatWasSaved()
    {
        var cut = Render<RoundsPanel>();
        cut.Find("button.add-round").Click();
        cut.Find("button.add-round").Click();

        cut.FindAll("input.round-start")[1].Change(Day.AddHours(12).AddMinutes(10).ToString("yyyy-MM-ddTHH:mm:ss"));
        cut.Find("button.save-rounds").Click();

        cut.Find(".rounds-error").TextContent.Should().NotBeNullOrWhiteSpace();
        _runtime.ThemeRounds.Should().BeEmpty();
    }

    [Fact]
    public void Jury_ShouldSeeTheRoundsWithoutEditing()
    {
        _runtime.SetRounds(RuntimeSettingsService.GenerateRounds(Day.AddHours(12), Day.AddHours(13), 30));
        _auth.SetAuthorized("Jury");
        _auth.SetRoles(ScoutingAutomation.Web.Auth.AppAuth.JuryRole);

        var cut = Render<RoundsPanel>();

        cut.FindAll("tr.round-row").Should().HaveCount(2);
        cut.FindAll("input").Should().BeEmpty();
        cut.FindAll("button.save-rounds").Should().BeEmpty();
    }

    [Theory]
    [InlineData("3", new[] { "3" })]
    [InlineData("T3, t4;5", new[] { "3", "4", "5" })]
    [InlineData(" ", new string[0])]
    public void ParseThemes_ShouldAcceptCommonWritings(string text, string[] expected)
    {
        RoundsPanel.ParseThemes(text).Should().Equal(expected);
    }
}
