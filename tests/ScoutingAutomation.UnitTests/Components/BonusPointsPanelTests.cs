using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Exceptions;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Domain.Models;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class BonusPointsPanelTests : BunitContext
{
    private readonly TempDirectory _temp = new();
    private readonly RuntimeSettingsService _runtime;
    private readonly List<GroupBonusPoints> _points = new();
    private readonly IGroupRepository _groups = Substitute.For<IGroupRepository>();
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly Group _wolves = new() { Name = "De Wolven", SlackChannelId = "C1" };
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public BonusPointsPanelTests()
    {
        _points.Add(new GroupBonusPoints { GroupId = _wolves.Id, CategoryKey = "avonturiers", Points = 2 });
        _groups.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { _wolves });
        _groups.GetBonusPointsAsync(Arg.Any<CancellationToken>()).Returns(_ => _points.ToList());
        _runtime = new RuntimeSettingsService(
            Options.Create(new ScoutingSettings { RuntimeSettingsFile = Path.Combine(_temp.Path, "runtime-settings.json") }),
            NullLogger<RuntimeSettingsService>.Instance);
        Services.AddSingleton(_runtime);
        Services.AddSingleton(_groups);
        Services.AddSingleton(_notifier);
        _auth = AddAuthorization();
        _auth.SetAuthorized("Admin");
        _auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
    }

    [Fact]
    public void Save_ByAnAdmin_ShouldStoreThePointsAndRefreshScoreboards()
    {
        var cut = Render<BonusPointsPanel>();
        cut.Find("input.bonus-avonturiers").GetAttribute("value").Should().Be("2");

        cut.Find("input.bonus-reisbureau").Change("9");
        cut.Find(".save-bonus").Click();

        _groups.Received(1).SetBonusPointsAsync(
            Arg.Is<IReadOnlyCollection<BonusPointsChange>>(c => c.Single() == new BonusPointsChange(_wolves.Id, "reisbureau", 0, 9)),
            Arg.Any<CancellationToken>());
        _notifier.Received(1).NotifyChanged();
        cut.Find(".bonus-message").TextContent.Should().Be("Opgeslagen.");
    }

    [Fact]
    public void Save_WithANegativeValue_ShouldSaveNothing()
    {
        var cut = Render<BonusPointsPanel>();

        cut.Find("input.bonus-avonturiers").Change("-3");
        cut.Find(".save-bonus").Click();

        NothingSaved();
        cut.Find(".bonus-message").TextContent.Should().Contain("De Wolven").And.Contain("niets opgeslagen");
    }

    [Fact]
    public void Save_AboveTheMaximum_ShouldSaveNothing()
    {
        var cut = Render<BonusPointsPanel>();

        cut.Find("input.bonus-avonturiers").Change("5000");
        cut.Find(".save-bonus").Click();

        NothingSaved();
        cut.Find(".bonus-message").TextContent.Should().Contain("1000");
    }

    [Fact]
    public void Save_WithoutChanges_ShouldNotSaveOrRefresh()
    {
        var cut = Render<BonusPointsPanel>();

        cut.Find(".save-bonus").Click();

        NothingSaved();
        _notifier.DidNotReceive().NotifyChanged();
        cut.Find(".bonus-message").TextContent.Should().Be("Niets gewijzigd.");
    }

    [Fact]
    public void Save_ChangedByAnotherAdmin_ShouldExplainAndShowTheNewValues()
    {
        _groups.SetBonusPointsAsync(Arg.Any<IReadOnlyCollection<BonusPointsChange>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _points[0].Points = 7;
                return Task.FromException(new BonusPointsConflictException("De Wolven"));
            });
        var cut = Render<BonusPointsPanel>();

        cut.Find("input.bonus-reisbureau").Change("4");
        cut.Find(".save-bonus").Click();

        cut.Find(".bonus-message").TextContent.Should().Contain("iemand anders");
        cut.Find("input.bonus-avonturiers").GetAttribute("value").Should().Be("7");
        _notifier.DidNotReceive().NotifyChanged();
    }

    [Fact]
    public void Save_WhenTheDatabaseFails_ShouldShowAMessageInsteadOfBreakingThePage()
    {
        _groups.SetBonusPointsAsync(Arg.Any<IReadOnlyCollection<BonusPointsChange>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Microsoft.EntityFrameworkCore.DbUpdateException("database is locked"));
        var cut = Render<BonusPointsPanel>();

        cut.Find("input.bonus-reisbureau").Change("4");
        cut.Find(".save-bonus").Click();

        cut.Find(".bonus-message").TextContent.Should().Contain("mislukt").And.Contain("niets opgeslagen");
    }

    private void NothingSaved() =>
        _groups.DidNotReceive().SetBonusPointsAsync(Arg.Any<IReadOnlyCollection<BonusPointsChange>>(), Arg.Any<CancellationToken>());

    [Fact]
    public void CategoryAddedByAnotherAdmin_ShouldAddAColumnAndKeepWhatWasTyped()
    {
        var cut = Render<BonusPointsPanel>();
        cut.Find("input.bonus-reisbureau").Change("6");

        _runtime.AddBonusCategory("fotowedstrijd", "Fotowedstrijd").Should().BeNull();

        cut.WaitForAssertion(() => cut.FindAll("input.bonus-fotowedstrijd").Should().ContainSingle());
        cut.Find("input.bonus-reisbureau").GetAttribute("value").Should().Be("6", "a new column must not wipe an unsaved entry");
        cut.Markup.Should().Contain("Fotowedstrijd");
    }

    [Fact]
    public void CategoryRemoved_ShouldDropItsColumn()
    {
        var cut = Render<BonusPointsPanel>();

        _runtime.RemoveBonusCategory("avonturiers").Should().BeTrue();

        cut.WaitForAssertion(() => cut.FindAll("input.bonus-avonturiers").Should().BeEmpty());
        cut.FindAll("input.bonus-reisbureau").Should().ContainSingle();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _temp.Dispose();
    }

    [Fact]
    public void Jury_ShouldNotSeeThePanel()
    {
        _auth.SetRoles();

        var cut = Render<BonusPointsPanel>();

        cut.FindAll(".bonus-points").Should().BeEmpty();
    }
}
