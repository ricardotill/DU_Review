using Bunit;
using FluentAssertions;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SlackSimulatorPageTests : PageTestBase
{
    private IRenderedComponent<SlackSimulator> RenderSimulator()
    {
        var cut = Render<SlackSimulator>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Bestand uploaden"));
        return cut;
    }

    private static IReadOnlyList<string> Log(IRenderedComponent<SlackSimulator> cut) =>
        cut.FindAll(".font-monospace.small > div.mb-1").Select(d => d.TextContent).ToList();

    [Fact]
    public void SimulationOff_ShouldOnlyShowNotice()
    {
        Settings.Slack.SimulationMode = false;

        var cut = Render<SlackSimulator>();

        cut.Markup.Should().Contain("alleen beschikbaar als");
        cut.FindAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Upload_ShouldRegisterSubmissionAndLogIt()
    {
        Seed();
        var cut = RenderSimulator();
        cut.FindAll("select")[0].Change("C3");
        cut.FindAll("textarea")[0].Change("Opdracht 3: gelukt");

        cut.Find("button.btn-scout-primary").Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains("OK")));
        WithDb(db => db.Submissions.Count(s => s.GroupId == Foxes.Id)).Should().Be(1);
        cut.FindAll("select")[1].QuerySelectorAll("option").Should().Contain(o => o.TextContent.Contains("De Vossen"));
    }

    [Theory]
    [InlineData(1, "verslag")] // PDF preset
    [InlineData(2, "knopen_demo")] // video preset
    public void Presets_ShouldFillFileAndMessage(int preset, string expectedFile)
    {
        Seed();
        var cut = RenderSimulator();

        cut.FindAll("a.dropdown-item")[preset == 1 ? 3 : 2].Click();
        cut.Find("button.btn-scout-primary").Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains(expectedFile)));
        cut.FindAll("a.dropdown-item")[0].Click();
        cut.FindAll("a.dropdown-item")[1].Click();
    }

    [Fact]
    public void UploadWithoutNumber_ShouldLogWarning()
    {
        Seed();
        var cut = RenderSimulator();
        cut.FindAll("textarea")[0].Change("hallo");

        cut.Find("button.btn-scout-primary").Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains("LET OP")));
    }

    [Fact]
    public void Reaction_ByReviewer_ShouldApprove()
    {
        var seeded = Seed();
        var cut = RenderSimulator();
        cut.FindAll("select")[1].Change(seeded.Pending.Id.ToString());
        cut.Find("input[list=configured-reviewers]").Change("U_JURY");

        cut.FindAll("button").Single(b => b.TextContent.Contains("Emoji :")).Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains("Status is nu Goedgekeurd")));
        WithDb(db => db.Submissions.Single(s => s.Id == seeded.Pending.Id).Status).Should().Be(SubmissionStatus.Approved);
    }

    [Fact]
    public void Reaction_ByNonReviewer_ShouldLogOutcome()
    {
        Seed();
        var cut = RenderSimulator();

        cut.Find("button.btn-outline-primary").Click();
        cut.Find("button.btn-outline-primary").ClassList.Should().Contain("active");
        cut.Find("button.btn-outline-success").Click();
        cut.Find("button.btn-outline-success").ClassList.Should().Contain("active");
        cut.Find("button.btn-outline-danger").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Emoji :")).Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains("geen jurylid")));
    }

    [Fact]
    public void Reaction_OnDeletedSubmission_ShouldLogError()
    {
        var seeded = Seed();
        var cut = RenderSimulator();
        cut.FindAll("select")[1].Change(seeded.Pending.Id.ToString());
        WithDb(db => { db.Submissions.Remove(db.Submissions.Single(s => s.Id == seeded.Pending.Id)); db.SaveChanges(); });

        cut.FindAll("button").Single(b => b.TextContent.Contains("Emoji :")).Click();

        cut.WaitForAssertion(() => Log(cut).Should().Contain(l => l.Contains("FOUT")));
    }

    [Fact]
    public void ClearLog_ShouldEmptyTheLog()
    {
        Seed();
        var cut = RenderSimulator();
        cut.Find("button.btn-scout-primary").Click();
        cut.WaitForAssertion(() => Log(cut).Should().NotBeEmpty());
        // Wait until the upload is done, or its result lands in the log after clearing
        cut.WaitForAssertion(() => cut.Find("button.btn-scout-primary").TextContent.Should().Contain("Upload plaatsen"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Wissen").Click();

        Log(cut).Should().BeEmpty();
        cut.Markup.Should().Contain("Nog niets.");
    }

    [Fact]
    public void WithoutData_ShouldStillRender()
    {
        var cut = RenderSimulator();

        cut.FindAll("select")[0].QuerySelectorAll("option").Should().BeEmpty();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Emoji :")).HasAttribute("disabled").Should().BeTrue();
    }
}
