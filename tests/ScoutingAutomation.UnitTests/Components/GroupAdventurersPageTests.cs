using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Adventurers;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// A group's own Avonturiers page: the theme round of now, and its open locations by status. Locations 1 through 3 are
/// open for every group; a check-in at 5 is not open yet.
/// </summary>
public class GroupAdventurersPageTests : PageTestBase
{
    public GroupAdventurersPageTests()
    {
        var now = DateTimeOffset.UtcNow.ToGameTime().DateTime;
        Settings.Adventurers = new AdventurersSettings { Enabled = true, InitialOpenLocations = 3 };
        Settings.Adventurers.Rounds.Add(new ThemeRoundConfig { Number = 2, Start = now.AddMinutes(-10), End = now.AddMinutes(20), Themes = { "2", "3" }, Message = "Iets met water" });
        WithDb(db =>
        {
            db.Groups.AddRange(Wolves, Owls);
            db.Locations.AddRange(Enumerable.Range(1, 6).Select(n => new Location { Number = n, Name = $"Plek {n}", Latitude = 51, Longitude = 5 }));
            db.Submissions.AddRange(
                Post(Wolves, SubmissionKind.CheckIn, "1", SubmissionStatus.Awesome),
                Post(Wolves, SubmissionKind.CheckIn, "2", SubmissionStatus.Rejected),
                Post(Wolves, SubmissionKind.CheckIn, "5", SubmissionStatus.Approved),
                Post(Wolves, SubmissionKind.Theme, "2", SubmissionStatus.Pending),
                // The Owls approved location 3; that must not show on the Wolves' page
                Post(Owls, SubmissionKind.CheckIn, "3", SubmissionStatus.Approved));
            db.SaveChanges();
        });
    }

    private static Submission Post(Group group, SubmissionKind kind, string code, SubmissionStatus status) => new()
    {
        GroupId = group.Id, Kind = kind, AssignmentCode = code, Status = status, FileName = "a.jpg", StoragePath = "/x/a.jpg",
        ContentType = "image/jpeg", Submitter = "U", SlackChannelId = "CAV", SlackMessageId = Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
    };

    private static IEnumerable<string?> Numbers(IRenderedComponent<GroupAdventurersPage> cut, string list) =>
        cut.FindAll($".{list} .location-item").Select(e => e.GetAttribute("data-number"));

    [Fact]
    public void Group_ShouldSeeItsLocationsByStatus()
    {
        LogInAsGroup(Wolves);

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => Numbers(cut, "approved-locations").Should().Equal("1"));
        Numbers(cut, "rejected-locations").Should().Equal("2");
        Numbers(cut, "open-locations").Should().Equal("3");
        Numbers(cut, "waiting-locations").Should().BeEmpty();
        cut.Find(".closed-checkins").TextContent.Should().Contain("5");
        cut.Markup.Should().NotContain("Plek 4").And.NotContain("Plek 6", "locations that are not open stay hidden");
        cut.FindAll(".group-picker").Should().BeEmpty();
    }

    [Fact]
    public void Group_ShouldSeeTheRunningRoundAndItsOwnThemes()
    {
        LogInAsGroup(Wolves);

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => cut.Find(".round-themes").TextContent.Should().Contain("Ronde 2").And.Contain("half-uur opdrachten 2 en 3"));
        cut.Find(".round-message").TextContent.Should().Be("Iets met water");
        cut.Find(".theme-status[data-theme='2']").TextContent.Should().Contain("Open");
        cut.Find(".theme-status[data-theme='3']").TextContent.Should().Contain("nog niet ingestuurd");
    }

    [Fact]
    public void Group_BeforeTheAvonturiersStart_ShouldSeeNoMapUntilTheyStart()
    {
        Settings.Adventurers.StartAnnouncement.PostAt = DateTimeOffset.UtcNow.AddHours(1).ToGameTime().DateTime;
        LogInAsGroup(Wolves);

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => cut.Find(".not-started").TextContent.Should().Contain("zodra de Avonturiers beginnen"));
        cut.Markup.Should().NotContain("Plek 1");
        cut.FindAll(".current-round").Should().BeEmpty();

        Services.GetRequiredService<RuntimeSettingsService>().SetAdventurersPostTime(false, DateTimeOffset.UtcNow.AddMinutes(-1).ToGameTime().DateTime);
        cut.WaitForAssertion(() => Numbers(cut, "approved-locations").Should().Equal("1"));
        cut.FindAll(".not-started").Should().BeEmpty();
    }

    [Fact]
    public void Jury_BeforeTheAvonturiersStart_ShouldStillSeeTheMap()
    {
        Settings.Adventurers.StartAnnouncement.PostAt = DateTimeOffset.UtcNow.AddHours(1).ToGameTime().DateTime;
        LogInAsJury();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"onze-kaart?group={Wolves.Id}");

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => Numbers(cut, "approved-locations").Should().Equal("1"));
        cut.Find(".hidden-until-start").TextContent.Should().Contain("Groepen zien hun kaart pas");
    }

    [Fact]
    public void Jury_ShouldPickAGroup()
    {
        LogInAsJury();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"onze-kaart?group={Owls.Id}");

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => Numbers(cut, "approved-locations").Should().Equal("3"));
        cut.Find(".group-picker");
    }

    [Fact]
    public void WithoutARound_ShouldSaySo()
    {
        Settings.Adventurers!.Rounds.Clear();
        LogInAsGroup(Wolves);

        var cut = Render<GroupAdventurersPage>();

        cut.WaitForAssertion(() => cut.Find(".no-round"));
    }

    [Fact]
    public void MapPopups_ForAGroup_ShouldNotLoadFilesButForTheJuryShowThePhoto()
    {
        LogInAsGroup(Wolves);
        var group = Render<GroupAdventurersPage>();
        group.WaitForAssertion(() => Numbers(group, "approved-locations").Should().Equal("1"));

        group.FindComponent<MapView>().Instance.Markers.SelectMany(m => m.Photos).Should().NotContain(p => p.Url.Contains("/api/files"));

        LogInAsJury();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"onze-kaart?group={Wolves.Id}");
        var jury = Render<GroupAdventurersPage>();
        jury.WaitForAssertion(() => jury.FindComponent<MapView>().Instance.Markers.SelectMany(m => m.Photos).Should().Contain(p => p.Url.Contains("/api/files")));
    }
}
