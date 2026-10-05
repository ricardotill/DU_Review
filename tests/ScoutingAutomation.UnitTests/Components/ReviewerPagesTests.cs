using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Panels;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>The review queue, reviewing one assignment in one pass, the slideshow choices and the review rules.</summary>
public class ReviewerPagesTests : PageTestBase
{
    [Fact]
    public void Queue_ShouldListOpenMessagesOldestFirst()
    {
        var seeded = Seed();

        var cut = Render<Queue>();

        cut.WaitForAssertion(() => cut.FindAll(".queue-row").Should().HaveCount(2));
        var rows = cut.FindAll(".queue-row");
        rows[0].TextContent.Should().Contain("De Bosuilen").And.Contain("#1").And.Contain("30 min");
        rows[1].TextContent.Should().Contain("#?");
        // Simulation has no Slack workspace: the row opens the details instead
        rows[0].GetAttribute("href").Should().Be($"submissions/{seeded.Pending.Id}");
        cut.FindAll(".slack-app-link").Should().BeEmpty();
    }

    [Fact]
    public void Queue_ShouldPutMessagesThatNoLongerCountAside()
    {
        Seed();
        var late = NewSubmission(Wolves, 1, SubmissionStatus.Pending, "nog1.jpg", DateTimeOffset.UtcNow.AddMinutes(-5), "image/jpeg", 10);
        WithDb(db => { db.Submissions.Add(late); db.SaveChanges(); });

        var cut = Render<Queue>();

        cut.WaitForAssertion(() => cut.Find(".no-longer-counting").TextContent.Should().Contain("1 bericht telt niet meer").And.Contain("De Wolven"));
        cut.FindAll(".glass-panel .queue-row").Should().HaveCount(3);
    }

    [Fact]
    public void Queue_ShouldMarkMessagesWaitingLongerThanTheReminderTime()
    {
        Seed();
        Settings.ReviewReminder.AfterMinutes = 20;

        var cut = Render<Queue>();

        cut.WaitForAssertion(() => cut.FindAll(".queue-row.overdue").Should().ContainSingle()
            .Which.TextContent.Should().Contain("De Bosuilen"));
        cut.Markup.Should().Contain("Herinnering aan de jury na 20 min");
    }

    [Fact]
    public void Queue_GroupFilter_ShouldNarrowTheList()
    {
        Seed();
        var cut = Render<Queue>();
        cut.WaitForAssertion(() => cut.FindAll(".queue-row").Should().HaveCount(2));

        cut.Find("#queueGroup").Change(Wolves.Id.ToString());

        cut.Markup.Should().Contain("Alles is beoordeeld.");
    }

    [Fact]
    public void AssignmentOpenOnly_ShouldShowOnlyOpenSubmissions()
    {
        Seed();
        Services.GetRequiredService<NavigationManager>().NavigateTo("assignments/1?open=true");

        var cut = Render<AssignmentDetail>(p => p.Add(a => a.Code, "1"));

        cut.WaitForAssertion(() => cut.FindAll(".submission-row").Should().ContainSingle()
            .Which.TextContent.Should().Contain("DBU 1"));
        cut.Find("#openOnly").HasAttribute("checked").Should().BeTrue();
        cut.Find("a[data-hotkey=ArrowRight]").GetAttribute("href").Should().Be("assignments/2?open=true");
        cut.Markup.Should().NotContain("Niet ingezonden:");
    }

    [Fact]
    public void Assignment_NextWithOpen_ShouldSkipAssignmentsWithoutOpenSubmissions()
    {
        Seed();
        var openThree = NewSubmission(Owls, 3, SubmissionStatus.Pending, "drie.jpg", DateTimeOffset.UtcNow, "image/jpeg", 10);
        WithDb(db => { db.Submissions.Add(openThree); db.SaveChanges(); });

        var cut = Render<AssignmentDetail>(p => p.Add(a => a.Code, "1"));

        cut.WaitForAssertion(() => cut.Find("a[data-hotkey=n]").GetAttribute("href").Should().Be("assignments/3"));
        cut.FindAll(".submission-row").Should().HaveCount(3);
    }

    [Fact]
    public void Slideshow_AwesomeOnly_ShouldNarrowTheShow()
    {
        Seed();
        var cut = Render<Slideshow>();
        cut.WaitForAssertion(() => cut.Find(".item-count").TextContent.Should().Be("3 items"));

        cut.Find("#slideshowAwesome").Change(true);

        cut.Find(".item-count").TextContent.Should().Be("1 item");
    }

    [Fact]
    public void Slideshow_Assignment_ShouldNarrowTheShowAndComeFromTheQuery()
    {
        Seed();
        Services.GetRequiredService<NavigationManager>().NavigateTo("slideshow?assignment=2");

        var cut = Render<Slideshow>();

        cut.WaitForAssertion(() => cut.Find(".item-count").TextContent.Should().Be("1 item"));
        cut.Find("#slideshowAssignment").Change("");
        cut.Find(".item-count").TextContent.Should().Be("3 items");
    }

    [Fact]
    public void ReviewRules_AdminCanSwitchBlockingAndSetTheReminder()
    {
        Settings.ReviewReminder.ChannelId = "C_JURY";
        var runtime = Services.GetRequiredService<RuntimeSettingsService>();
        var cut = Render<ReviewRulesPanel>();

        cut.Find("#blockAttempts").Change(true);
        runtime.BlockAttemptsAfterApproval.Should().BeTrue();
        cut.Markup.Should().Contain("Aangepast in de app");

        cut.Find(".reminder-minutes").Change("10");
        cut.Find(".reminder-form").Submit();
        runtime.ReviewReminderMinutes.Should().Be(10);
        cut.Markup.Should().Contain("10 minuten").And.Contain("#C_JURY");

        cut.Find(".reminder-minutes").Change("999");
        cut.Find(".reminder-form").Submit();
        cut.Markup.Should().Contain("Kies tussen 1 en 240 minuten");
    }

    [Fact]
    public void ReviewRules_JuryOnlySeesTheSettings()
    {
        LogInAsJury();

        var cut = Render<ReviewRulesPanel>();

        cut.FindAll("#blockAttempts").Should().BeEmpty();
        cut.FindAll(".reminder-form").Should().BeEmpty();
        cut.Markup.Should().Contain("Uit: opnieuw insturen mag altijd");
    }
}
