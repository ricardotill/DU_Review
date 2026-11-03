using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Workflow;
using ScoutingAutomation.Web.Components.Assignments;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Panels;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>
/// What a group sees of the assignments its Avonturiers opened, how it hears about new ones, and how the organisation
/// opens locations. 10 assignments, 2 open at the start, 4 locations: each counted location opens two more.
/// </summary>
public class GroupAssignmentsPageTests : PageTestBase
{
    public GroupAssignmentsPageTests()
    {
        Settings.Adventurers = new AdventurersSettings { Enabled = true, InitialOpenAssignments = 2, InitialOpenLocations = 2 };
        WithDb(db =>
        {
            db.Groups.AddRange(Wolves, Owls);
            db.Assignments.AddRange(Enumerable.Range(1, 10).Select(n => new Assignment
            {
                Code = n.ToString(), StandardPoints = 1, AwesomePoints = 2, Title = $"Titel {n}", Description = $"Geheime tekst {n}"
            }));
            db.Locations.AddRange(Enumerable.Range(1, 4).Select(n => new Location { Number = n, Name = $"Plek {n}", Latitude = 51, Longitude = 5 }));
            // The Owls checked in once: their assignment 3 and 4 are open, the Wolves' not
            db.Submissions.Add(CheckIn(Owls, 1));
            db.AssignmentImages.Add(new AssignmentImage { AssignmentCode = "1", FileName = "voorbeeld.jpg", StoragePath = "/x/voorbeeld.jpg" });
            db.AssignmentImages.Add(new AssignmentImage { AssignmentCode = "5", FileName = "geheim.jpg", StoragePath = "/x/geheim.jpg" });
            db.SaveChanges();
        });
    }

    private Submission CheckIn(Group group, int location) => new()
    {
        GroupId = group.Id, Kind = SubmissionKind.CheckIn, AssignmentCode = location.ToString(), Status = SubmissionStatus.Approved,
        FileName = "a.jpg", StoragePath = "/x/a.jpg", ContentType = "image/jpeg", Submitter = "U", SlackChannelId = "CAV",
        SlackMessageId = Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Group_ShouldSeeOnlyItsOpenAssignmentsAndNoLockedText()
    {
        LogInAsGroup(Wolves);

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Select(e => e.GetAttribute("data-code")).Should().Equal("1", "2"));
        cut.Find(".open-count").TextContent.Should().Contain("2").And.Contain("van 10");
        cut.Markup.Should().Contain("Titel 1").And.NotContain("Geheime tekst 3").And.NotContain("geheim.jpg");
        cut.FindAll(".assignment-image-thumb img").Select(i => i.GetAttribute("alt")).Should().Equal("voorbeeld.jpg");
        cut.FindAll(".group-picker").Should().BeEmpty("a group cannot look at other groups");
    }

    [Fact]
    public void Group_WithAnOpenSuperLocationLeft_ShouldSeeWhatItOpens()
    {
        Services.GetRequiredService<RuntimeSettingsService>().SetSuperLocations(true, new[] { 2 });
        LogInAsGroup(Wolves);

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.Find(".next-super").TextContent.Should().Contain("Een supercoördinaat opent er"));
    }

    [Fact]
    public void Group_WithoutAnOpenSuperLocationLeft_ShouldNotMentionIt()
    {
        // Location 4 is super, but the Wolves have only locations 1 and 2 open
        Services.GetRequiredService<RuntimeSettingsService>().SetSuperLocations(true, new[] { 4 });
        LogInAsGroup(Wolves);

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Should().NotBeEmpty());
        cut.FindAll(".next-super").Should().BeEmpty();
    }

    [Fact]
    public void Group_TodoOnly_ShouldKeepWhatWasNeverSentInAndHideWhatIsDone()
    {
        WithDb(db =>
        {
            var done = CheckIn(Wolves, 0);
            done.Kind = SubmissionKind.Assignment;
            done.AssignmentCode = "1";
            db.Submissions.Add(done);
            db.SaveChanges();
        });
        LogInAsGroup(Wolves);
        var cut = Render<GroupAssignments>();
        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Should().HaveCount(2));
        cut.FindAll("[data-code='2'] .badge-status").Should().BeEmpty("nothing was sent in for it");

        cut.Find("input.todo-only").Change(true);

        cut.FindAll(".open-assignment").Select(e => e.GetAttribute("data-code")).Should().Equal("2");
    }

    [Fact]
    public void Group_AddressOfAnotherGroup_ShouldStillShowItsOwn()
    {
        LogInAsGroup(Wolves);
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"mijn-opdrachten?group={Owls.Id}");

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Should().HaveCount(2, "the Wolves have only the first two"));
    }

    [Fact]
    public void Group_BeforeTheGameStarts_ShouldSeeNoAssignmentsUntilItStarts()
    {
        Settings.EventStart = DateTimeOffset.UtcNow.AddHours(1).ToGameTime().DateTime;
        LogInAsGroup(Wolves);

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.Find(".not-started").TextContent.Should().Contain("zodra het spel begint"));
        cut.FindAll(".open-assignment").Should().BeEmpty();
        cut.Markup.Should().NotContain("Titel 1");

        // The start moved to now: the page shows the assignments without a reload
        Services.GetRequiredService<RuntimeSettingsService>().SetEventStart(DateTimeOffset.UtcNow.AddMinutes(-1).ToGameTime().DateTime);
        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Should().HaveCount(2));
        cut.FindAll(".not-started").Should().BeEmpty();
    }

    [Fact]
    public void Jury_BeforeTheGameStarts_ShouldStillSeeTheAssignments()
    {
        Settings.EventStart = DateTimeOffset.UtcNow.AddHours(1).ToGameTime().DateTime;
        LogInAsJury();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"mijn-opdrachten?group={Wolves.Id}");

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Should().HaveCount(2));
        cut.Find(".hidden-until-start").TextContent.Should().Contain("Groepen zien de opdrachten pas");
    }

    [Fact]
    public void Jury_ShouldPickAGroup()
    {
        LogInAsJury();
        Services.GetRequiredService<Bunit.TestDoubles.BunitNavigationManager>().NavigateTo($"mijn-opdrachten?group={Owls.Id}");

        var cut = Render<GroupAssignments>();

        cut.WaitForAssertion(() => cut.FindAll(".open-assignment").Select(e => e.GetAttribute("data-code")).Should().Equal("1", "2", "3", "4"));
        cut.Find(".group-picker");
    }

    [Fact]
    public void Unlock_ShouldReachOnlyThePagesOfThatGroup()
    {
        LogInAsGroup(Wolves);
        var cut = Render<GroupToastHost>();
        var notifier = Services.GetRequiredService<ISubmissionChangeNotifier>();

        notifier.NotifyUnlocked(new AssignmentsUnlockedNotification(Owls.Id, ["3", "4"], 4, 10));
        cut.FindAll(".unlock-toast").Should().BeEmpty();

        notifier.NotifyUnlocked(new AssignmentsUnlockedNotification(Wolves.Id, ["3", "4"], 4, 10));
        cut.WaitForAssertion(() => cut.Find(".unlock-toast").TextContent.Should().Contain("Opdracht 3 en 4 zijn open").And.Contain("4 van de 10"));
    }

    [Fact]
    public void Unlock_AfterTheGroupKeyIsWithdrawn_ShouldNotShow()
    {
        LogInAsGroup(Wolves);
        var cut = Render<GroupToastHost>();

        Auth.SetNotAuthorized();
        Services.GetRequiredService<ISubmissionChangeNotifier>().NotifyUnlocked(new AssignmentsUnlockedNotification(Wolves.Id, ["3"], 3, 10));

        cut.FindAll(".unlock-toast").Should().BeEmpty();
    }

    [Fact]
    public void AssignmentEditor_TooManyImages_ShouldSayHowManyInDutchAndStoreNothing()
    {
        LogInAsJury();
        var assignment = WithDb(db => db.Assignments.AsNoTracking().Single(a => a.Code == "1"));
        var cut = Render<AssignmentEditor>(p => p.Add(c => c.Assignment, assignment));
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

        cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>()
            .UploadFiles(Enumerable.Range(1, 21).Select(i => InputFileContent.CreateFromBinary(png, $"{i}.png", contentType: "image/png")).ToArray());

        // Assignment 1 already has one picture: room for 19 more
        cut.WaitForAssertion(() => cut.Find(".edit-message").TextContent.Should().Contain("Kies hoogstens 19 afbeeldingen"));
        WithDb(db => db.AssignmentImages.Count(i => i.AssignmentCode == "1")).Should().Be(1);
    }

    [Fact]
    public void OpenLocationsPanel_PlusOne_ShouldOpenTheNextLocation()
    {
        WithDb(db =>
        {
            db.Submissions.Add(CheckIn(Wolves, 3));
            db.SaveChanges();
        });
        var cut = Render<OpenLocationsPanel>();
        var wolvesRow = () => cut.FindAll(".open-locations tbody tr").Single(r => r.TextContent.Contains("De Wolven"));
        wolvesRow().TextContent.Should().Contain("1 / 0").And.Contain("2 van 10");

        wolvesRow().QuerySelector("button.open-plus")!.Click();

        cut.WaitForAssertion(() => wolvesRow().TextContent.Should().Contain("1 / 1").And.Contain("4 van 10"));
        WithDb(db => db.Groups.AsNoTracking().Single(g => g.Id == Wolves.Id).OpenLocations).Should().Be(3);
    }

    [Fact]
    public void OpenLocationsPanel_ForTheJury_ShouldChangeNothing()
    {
        LogInAsJury();
        var cut = Render<OpenLocationsPanel>();

        cut.FindAll("button.open-plus").First().Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Alleen in beheermodus"));
        WithDb(db => db.Groups.AsNoTracking().All(g => g.OpenLocations == null)).Should().BeTrue();
    }

    [Fact]
    public void AssignmentEditor_ForAGroup_ShouldBeRefused()
    {
        LogInAsGroup(Wolves);
        var assignment = WithDb(db => db.Assignments.AsNoTracking().Single(a => a.Code == "1"));
        var cut = Render<AssignmentEditor>(p => p.Add(c => c.Assignment, assignment));

        cut.Find("input.edit-title").Change("Gehackt");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".edit-message").TextContent.Should().Contain("Alleen voor de jury"));
        WithDb(db => db.Assignments.AsNoTracking().Single(a => a.Code == "1").Title).Should().Be("Titel 1");
    }

    [Fact]
    public void AssignmentEditor_ForTheJury_ShouldSave()
    {
        LogInAsJury();
        var assignment = WithDb(db => db.Assignments.AsNoTracking().Single(a => a.Code == "1"));
        var cut = Render<AssignmentEditor>(p => p.Add(c => c.Assignment, assignment));

        cut.Find("input.edit-title").Change("Nieuwe titel");
        cut.Find("input.edit-awesome").Change("5");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".edit-message").TextContent.Should().Contain("Opgeslagen"));
        var saved = WithDb(db => db.Assignments.AsNoTracking().Single(a => a.Code == "1"));
        (saved.Title, saved.AwesomePoints, saved.EditedAt.HasValue).Should().Be(("Nieuwe titel", 5, true));
    }
}
