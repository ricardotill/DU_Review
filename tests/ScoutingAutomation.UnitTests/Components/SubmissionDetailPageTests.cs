using Bunit;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SubmissionDetailPageTests : PageTestBase
{
    private IRenderedComponent<SubmissionDetail> RenderSubmission(Guid id)
    {
        var cut = Render<SubmissionDetail>(p => p.Add(c => c.Id, id));
        cut.WaitForAssertion(() => cut.FindAll(".spinner-border").Should().BeEmpty());
        return cut;
    }

    [Fact]
    public void Submission_ShouldShowPreviewDetailsAndHistory()
    {
        var seeded = Seed();

        var cut = RenderSubmission(seeded.Approved.Id);

        cut.Find("h1").TextContent.Should().Be("DWO 1.jpg");
        cut.Find(".preview img").GetAttribute("src").Should().Be($"/api/files/{seeded.Approved.Id}");
        cut.Find("a[href^='groups/']").TextContent.Should().Be("De Wolven");
        cut.Find("a[href='assignments/1']").TextContent.Should().Be("Assignment #1");
        cut.Find(".detail-list").TextContent.Should().Contain("vuur2.jpg").And.Contain("#C1");
        cut.Find(".slack-message").TextContent.Should().Contain("Opdracht 1");
        cut.FindAll(".timeline-item").Should().ContainSingle().Which.TextContent.Should().Contain(":thumbsup:");
        cut.FindAll(".alert-warning.d-flex").Should().BeEmpty("only unresolved uploads get the banner");
    }

    [Theory]
    [InlineData("video/mp4", ".preview video")]
    [InlineData("audio/mp4", ".preview audio")]
    [InlineData("application/pdf", ".preview a[target=_blank]")]
    public void Preview_ShouldMatchFileType(string contentType, string selector)
    {
        Seed();
        var submission = NewSubmission(Foxes, 3, SubmissionStatus.Pending, "x", DateTimeOffset.UtcNow, contentType, 1);
        WithDb(db => { db.Submissions.Add(submission); db.SaveChanges(); });

        var cut = RenderSubmission(submission.Id);

        cut.Find(selector);
        cut.Markup.Should().Contain("No reviews yet.");
    }

    [Fact]
    public void UnresolvedSubmission_ShouldExplainWhy()
    {
        var seeded = Seed();

        var cut = RenderSubmission(seeded.Unresolved.Id);

        cut.Find(".alert-warning.d-flex").TextContent.Should().Contain("No valid assignment number");
        cut.Markup.Should().Contain("Unresolved");
        cut.Find("h1").TextContent.Should().Be("raadsel.m4a");
    }

    [Fact]
    public void WithoutMessageTextOrStoragePath_ShouldUseFallbacks()
    {
        Seed();
        var submission = NewSubmission(Foxes, 3, SubmissionStatus.Pending, "origineel.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1);
        submission.MessageText = null;
        submission.StoragePath = "";
        WithDb(db => { db.Submissions.Add(submission); db.SaveChanges(); });

        var cut = RenderSubmission(submission.Id);

        cut.Find("h1").TextContent.Should().Be("origineel.jpg");
        cut.Find(".slack-message").TextContent.Should().Contain("(no text)");
    }

    [Fact]
    public void ReviewHistory_ShouldColourEachStatusAndShowLargeSizes()
    {
        Seed();
        var submission = NewSubmission(Foxes, 3, SubmissionStatus.Pending, "groot.jpg", DateTimeOffset.UtcNow, "image/jpeg", 3 * 1024 * 1024);
        var at = DateTimeOffset.UtcNow;
        foreach (var (status, i) in new[] { SubmissionStatus.Approved, SubmissionStatus.Awesome, SubmissionStatus.Rejected, SubmissionStatus.Pending }.Select((s, i) => (s, i)))
        {
            submission.Reviews.Add(new ScoutingAutomation.Domain.Entities.Review { Reviewer = "U_JURY", Status = status, CreatedAt = at.AddMinutes(-i) });
        }
        WithDb(db => { db.Submissions.Add(submission); db.SaveChanges(); });

        var cut = RenderSubmission(submission.Id);

        cut.FindAll(".timeline-item").Select(t => t.GetAttribute("style")).Should().Equal(
            "border-left-color: #10b981;", "border-left-color: #a855f7;", "border-left-color: #ef4444;", "border-left-color: #f59e0b;");
        cut.Find(".detail-list").TextContent.Should().MatchRegex(@"3[.,]0 MB");
        WithDb(db => db.Reviews.Count(r => r.SubmissionId == submission.Id)).Should().Be(4);
    }

    [Fact]
    public void SmallAndMediumFiles_ShouldUseBytesAndKilobytes()
    {
        var seeded = Seed();

        RenderSubmission(seeded.Unresolved.Id).Find(".detail-list").TextContent.Should().Contain("100 B");
        RenderSubmission(seeded.Pending.Id).Find(".detail-list").TextContent.Should().MatchRegex(@"2[.,]0 KB");
    }

    [Fact]
    public void UnknownSubmission_ShouldSayNotFound()
    {
        var cut = RenderSubmission(Guid.NewGuid());

        cut.Find("h1").TextContent.Should().Be("Submission not found");
    }

    [Fact]
    public void Override_ShouldRequireReasonAndConfirmationThenChangeStatus()
    {
        var seeded = Seed();
        var cut = RenderSubmission(seeded.Pending.Id);
        var button = () => cut.Find("details.override-panel button");

        button().HasAttribute("disabled").Should().BeTrue();
        cut.Find("details.override-panel select option:checked, details.override-panel select").Should().NotBeNull();

        cut.Find("details.override-panel select").Change(nameof(SubmissionStatus.Approved));
        cut.Find("details.override-panel textarea").Input("Reactie op verkeerd bericht");
        button().HasAttribute("disabled").Should().BeTrue("confirmation is still missing");

        cut.Find("#overrideConfirm").Change(true);
        button().HasAttribute("disabled").Should().BeFalse();
        button().Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Dashboard override"));
        cut.Find("h1 + div, .badge-status").TextContent.Should().Contain("Approved");
        cut.Find("details.override-panel textarea").GetAttribute("value").Should().BeNullOrEmpty();
        WithDb(db => db.Submissions.Single(s => s.Id == seeded.Pending.Id).Status).Should().Be(SubmissionStatus.Approved);
    }

    [Fact]
    public void Override_WhenItFails_ShouldShowError()
    {
        var seeded = Seed();
        var cut = RenderSubmission(seeded.Pending.Id);
        cut.Find("details.override-panel textarea").Input("reden");
        cut.Find("#overrideConfirm").Change(true);
        WithDb(db => { db.Submissions.Remove(db.Submissions.Single(s => s.Id == seeded.Pending.Id)); db.SaveChanges(); });

        cut.Find("details.override-panel button").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Override failed"));
    }

    [Fact]
    public void LiveUpdate_ShouldKeepChosenOverrideStatus()
    {
        var seeded = Seed();
        var cut = RenderSubmission(seeded.Pending.Id);
        cut.Find("details.override-panel select").Change(nameof(SubmissionStatus.Rejected));

        WithDb(db =>
        {
            var s = db.Submissions.Include(x => x.Reviews).Single(x => x.Id == seeded.Pending.Id);
            s.TransitionStatus(SubmissionStatus.Approved, s.StoragePath, "U_JURY", "thumbsup");
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.FindAll(".timeline-item").Should().ContainSingle(), TimeSpan.FromSeconds(5));
        cut.Find("details.override-panel select").GetAttribute("value").Should().Be(nameof(SubmissionStatus.Rejected));
    }

    [Fact]
    public void LiveUpdate_ToChosenOverrideStatus_ShouldPickAnotherStatus()
    {
        var seeded = Seed();
        var cut = RenderSubmission(seeded.Pending.Id);
        cut.Find("details.override-panel select").Change(nameof(SubmissionStatus.Approved));

        WithDb(db =>
        {
            var s = db.Submissions.Single(x => x.Id == seeded.Pending.Id);
            s.Status = SubmissionStatus.Approved;
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.Find("details.override-panel select").GetAttribute("value").Should().Be(nameof(SubmissionStatus.Pending)), TimeSpan.FromSeconds(5));
    }
}
