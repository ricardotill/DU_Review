using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Contracts.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Panels;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// With the Avonturiers on, their point category is fixed: it cannot be removed, its field on Extra punten is the
/// final game, and the computed total (half-hour assignments plus final game) shows next to it.
/// </summary>
public class AdventurersPointsTests : PageTestBase
{
    public AdventurersPointsTests()
    {
        Settings.Adventurers = new AdventurersSettings { Enabled = true, FinalMaxPoints = 14 };
        // An admin removed every category before the Avonturiers were switched on
        Settings.BonusCategories = new List<BonusCategoryConfig> { new() { Key = "reisbureau", Name = "Reisbureau" } };
        WithDb(db =>
        {
            db.Groups.Add(Wolves);
            db.GroupBonusPoints.Add(new GroupBonusPoints { GroupId = Wolves.Id, CategoryKey = "avonturiers", Points = 5 });
            db.SaveChanges();
        });
    }

    [Fact]
    public void Categories_ShouldShowTheAvonturiersAsFixed()
    {
        var cut = Render<BonusCategoriesPanel>();

        var avonturiers = cut.FindAll("tr.bonus-category").Single(r => r.TextContent.Contains("Avonturiers"));
        avonturiers.QuerySelector(".fixed-category").Should().NotBeNull();
        avonturiers.QuerySelector(".remove-category").Should().BeNull();
        cut.FindAll("tr.bonus-category").Single(r => r.TextContent.Contains("Reisbureau")).QuerySelector(".remove-category").Should().NotBeNull();
    }

    [Fact]
    public void Points_ShouldTakeTheFinalGameAndShowTheTotal()
    {
        var cut = Render<BonusPointsPanel>();

        cut.WaitForAssertion(() => cut.Find("input.bonus-avonturiers").GetAttribute("value").Should().Be("5"));
        cut.Find("th").ParentElement!.TextContent.Should().Contain("Avonturiers: eindspel");
        cut.Find(".adventurers-total").TextContent.Should().Contain("Totaal 5").And.Contain("(0 + 5)");
        cut.Find(".adventurers-hint");
    }

    [Fact]
    public void Points_FinalGameAboveTheMaximum_ShouldSaveNothing()
    {
        var cut = Render<BonusPointsPanel>();
        cut.WaitForAssertion(() => cut.Find("input.bonus-avonturiers"));

        cut.Find("input.bonus-avonturiers").Change("15");
        cut.Find(".save-bonus").Click();

        cut.Find(".bonus-message").TextContent.Should().Contain("hoogstens 14");
        WithDb(db => db.GroupBonusPoints.AsNoTracking().Single(p => p.CategoryKey == "avonturiers").Points).Should().Be(5);
    }

    [Fact]
    public void MaxPoints_ChangedInTheApp_ShouldCapTheTotalAndTheEindspelField()
    {
        var settings = Render<AdventurersSettingsPanel>();
        settings.Find("input.theme-max-input").Change("10");
        settings.Find("input.final-max-input").Change("3");
        settings.Find("form:has(.save-max-points)").Submit();

        var runtime = Services.GetRequiredService<RuntimeSettingsService>();
        (runtime.ThemeMaxPoints, runtime.FinalMaxPoints).Should().Be((10, 3));

        // The stored 5 stays, but counts as 3
        var points = Render<BonusPointsPanel>();
        points.WaitForAssertion(() => points.Find(".adventurers-total").TextContent.Should().Contain("Totaal 3").And.Contain("(0 + 3)"));
        points.Find("input.bonus-avonturiers").GetAttribute("max").Should().Be("3");

        // Another category can still be saved, although the old 5 is above the new maximum
        points.Find("input.bonus-reisbureau").Change("4");
        points.Find(".save-bonus").Click();
        points.WaitForAssertion(() => points.Find(".bonus-message").TextContent.Should().Be("Opgeslagen."));
    }
}
