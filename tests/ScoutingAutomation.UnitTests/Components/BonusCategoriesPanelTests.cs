using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class BonusCategoriesPanelTests : BunitContext
{
    private readonly TempDirectory _temp = new();
    private readonly RuntimeSettingsService _runtime;
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public BonusCategoriesPanelTests()
    {
        var options = Options.Create(new ScoutingSettings { RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json") });
        _runtime = new RuntimeSettingsService(options, NullLogger<RuntimeSettingsService>.Instance);
        Services.AddSingleton(_runtime);
        Services.AddSingleton(options);
        Services.AddSingleton(_notifier);
        _auth = AddAuthorization();
        _auth.SetAuthorized("Admin");
        _auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _temp.Dispose();
    }

    [Fact]
    public void Add_WithoutKey_ShouldTakeTheKeyFromTheNameAndRefreshScores()
    {
        var cut = Render<BonusCategoriesPanel>();

        cut.Find("input.new-name").Change("Foto wedstrijd!");
        cut.Find("form.add-category").Submit();

        _runtime.BonusCategories.Select(c => c.Key).Should().Equal("avonturiers", "reisbureau", "foto-wedstrijd");
        _notifier.Received(1).NotifyChanged();
        cut.FindAll("tr.bonus-category").Should().HaveCount(3);
    }

    [Fact]
    public void Add_Duplicate_ShouldShowWhy()
    {
        var cut = Render<BonusCategoriesPanel>();

        cut.Find("input.new-name").Change("Avonturiers");
        cut.Find("form.add-category").Submit();

        cut.Find(".category-error").TextContent.Should().Contain("al in gebruik");
        _notifier.DidNotReceive().NotifyChanged();
    }

    [Fact]
    public void Remove_ShouldAskFirst_ThenKeepThePointsButStopCountingThem()
    {
        var cut = Render<BonusCategoriesPanel>();

        cut.FindAll("button.remove-category")[1].Click();
        cut.Markup.Should().Contain("blijven bewaard");
        _runtime.BonusCategories.Should().HaveCount(2, "nothing is removed before the confirmation");

        cut.Find("button.confirm-remove").Click();

        _runtime.BonusCategories.Select(c => c.Key).Should().Equal("avonturiers");
        _notifier.Received(1).NotifyChanged();
    }

    [Fact]
    public void Rename_ShouldSaveTheNewName()
    {
        var cut = Render<BonusCategoriesPanel>();

        cut.FindAll("button.edit-category")[1].Click();
        cut.Find("input.edit-name").Change("Pubquiz");
        cut.Find("button.save-category").Click();

        _runtime.BonusCategories[1].Should().BeEquivalentTo(new { Key = "reisbureau", Name = "Pubquiz", ShortName = "Rb" });
    }

    [Fact]
    public void Jury_ShouldNotSeeThePanel()
    {
        _auth.SetRoles();

        Render<BonusCategoriesPanel>().FindAll(".bonus-categories").Should().BeEmpty();
    }
}
