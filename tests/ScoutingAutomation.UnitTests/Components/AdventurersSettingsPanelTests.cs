using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Panels;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>The super locations on the Avonturiers configuration panel: only the admin changes them, typos show.</summary>
public class AdventurersSettingsPanelTests : PageTestBase
{
    public AdventurersSettingsPanelTests()
    {
        Settings.Adventurers = new AdventurersSettings { Enabled = true };
        WithDb(db =>
        {
            db.Locations.AddRange(Enumerable.Range(1, 25).Select(n => new Location { Number = n, Name = $"Plek {n}", Latitude = 51, Longitude = 5 }));
            db.SaveChanges();
        });
    }

    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    private static void Save(IRenderedComponent<AdventurersSettingsPanel> cut, string numbers, bool enabled = true)
    {
        cut.Find(".super-enabled").Change(enabled);
        cut.Find(".super-numbers").Change(numbers);
        cut.Find(".save-super").Click();
    }

    [Fact]
    public void Admin_ShouldSaveTheNumbers()
    {
        var cut = Render<AdventurersSettingsPanel>();

        Save(cut, "20; 10\n10");

        cut.WaitForAssertion(() => Runtime.SuperLocations.Should().BeEquivalentTo(new[] { 10, 20 }));
        cut.Find(".super-current").TextContent.Should().Contain("Aan: locatie 10, 20");
        cut.FindAll(".super-unknown").Should().BeEmpty();
    }

    [Theory]
    [InlineData("10, abc", "'abc' is geen locatienummer.")]
    [InlineData("10, 0", "Kies locatienummers tussen 1 en")]
    [InlineData("-5", "Kies locatienummers tussen 1 en")]
    public void InvalidInput_ShouldSayWhyInDutchAndSaveNothing(string numbers, string message)
    {
        var cut = Render<AdventurersSettingsPanel>();

        Save(cut, numbers);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(message));
        Runtime.IsSuperLocationsChanged.Should().BeFalse();
    }

    [Fact]
    public void NumberThatIsNoLocation_ShouldBeShown()
    {
        Runtime.SetSuperLocations(true, new[] { 10, 105 });

        var cut = Render<AdventurersSettingsPanel>();

        cut.WaitForAssertion(() => cut.Find(".super-unknown").TextContent.Should().Contain("Geen locatie: 105"));
    }

    [Fact]
    public void EveryTenth_ShouldFillUpToTheHighestLocation()
    {
        var cut = Render<AdventurersSettingsPanel>();

        cut.WaitForAssertion(() => cut.Find(".super-every-ten").Click());

        cut.Find(".super-numbers").GetAttribute("value").Should().Be("10, 20");
    }

    [Fact]
    public void Jury_ShouldSeeTheSettingButNoForm()
    {
        Runtime.SetSuperLocations(true, new[] { 10 });
        LogInAsJury();

        var cut = Render<AdventurersSettingsPanel>();

        cut.Find(".super-current").TextContent.Should().Contain("locatie 10");
        cut.FindAll(".save-super").Should().BeEmpty();
    }
}
