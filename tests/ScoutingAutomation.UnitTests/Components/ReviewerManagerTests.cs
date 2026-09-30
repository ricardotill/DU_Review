using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class ReviewerManagerTests : PageTestBase
{
    private readonly Mock<ISlackClient> _slack = new();

    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    /// <summary>Connects a fake Slack, so names can be fetched.</summary>
    private void ConnectSlack()
    {
        _slack.Setup(s => s.IsConnected).Returns(true);
        _slack.Setup(s => s.GetUserNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(SlackUserLookup.Failed("user_not_found"));
        Services.RemoveAll<ISlackClient>();
        Services.AddSingleton(_slack.Object);
    }

    private static IReadOnlyList<string> Rows(IRenderedComponent<ReviewerManager> cut) =>
        cut.FindAll("li.reviewer").Select(r => r.TextContent.Replace("\n", " ").Trim()).ToList();

    [Fact]
    public void Reviewers_ShouldShowNamesIdsAndWhereTheyComeFrom()
    {
        Settings.ReviewerNames = new() { ["U_JURY"] = "Eva" };
        Runtime.AddReviewer("U_ADDED");

        var cut = Render<ReviewerManager>();

        var rows = cut.FindAll("li.reviewer");
        rows[0].QuerySelector(".reviewer-name")!.TextContent.Should().Be("Eva");
        rows[0].TextContent.Should().Contain("U_JURY");
        rows[0].QuerySelector(".bi-lock-fill").Should().NotBeNull();
        rows[0].QuerySelector(".remove-reviewer").Should().BeNull("reviewers from appsettings cannot be removed here");
        rows[1].QuerySelector(".reviewer-name")!.TextContent.Should().Be("no name");
        rows[1].QuerySelector(".remove-reviewer")!.GetAttribute("aria-label").Should().Be("Remove U_ADDED");
    }

    [Fact]
    public void Add_WithoutSlack_ShouldAddAndConfirm()
    {
        var cut = Render<ReviewerManager>();
        cut.Find("button.add-reviewer").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("button.fetch-names").Should().BeEmpty("names cannot be fetched without Slack");

        cut.Find("input[aria-label='Slack member ID']").Input("U04ABC123");
        cut.Find("form").Submit();

        Runtime.IsReviewer("U04ABC123").Should().BeTrue();
        cut.Find("input[aria-label='Slack member ID']").GetAttribute("value").Should().BeEmpty();
        cut.Find(".reviewer-status .text-success").TextContent.Should().Be("U04ABC123 is now a reviewer.");
    }

    [Fact]
    public void Add_WithSlack_ShouldFetchTheName()
    {
        ConnectSlack();
        _slack.Setup(s => s.GetUserNameAsync("U04ABC123", It.IsAny<CancellationToken>())).ReturnsAsync(SlackUserLookup.Found("Piet"));
        var cut = Render<ReviewerManager>();

        cut.Find("input[aria-label='Slack member ID']").Input("U04ABC123");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".reviewer-status .text-success").TextContent.Should().Be("Piet is now a reviewer."));
        Runtime.NameOf("U04ABC123").Should().Be("Piet");
    }

    [Fact]
    public void Add_UnknownToSlack_ShouldWarn()
    {
        ConnectSlack();
        var cut = Render<ReviewerManager>();

        cut.Find("input[aria-label='Slack member ID']").Input("U_TYPO");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Find(".reviewer-status .text-danger").TextContent.Should().Contain("Slack does not know U_TYPO"));
        Runtime.IsReviewer("U_TYPO").Should().BeTrue("the reviewer is still added; the warning points out a possible typo");
    }

    [Fact]
    public void Add_Invalid_ShouldShowWhy()
    {
        var cut = Render<ReviewerManager>();

        cut.Find("input[aria-label='Slack member ID']").Input("niet geldig");
        cut.Find("form").Submit();

        cut.Find(".reviewer-status .text-danger").TextContent.Should().Contain("Slack member ID");
    }

    [Fact]
    public void EditName_ShouldSaveCancelAndValidate()
    {
        var cut = Render<ReviewerManager>();

        cut.Find("button.edit-name").Click();
        cut.Find("input[aria-label='Name of U_JURY']").Input(new string('x', RuntimeSettingsService.MaxNameLength + 5));
        cut.Find("li.reviewer form").Submit();
        cut.Find(".reviewer-status .text-danger").TextContent.Should().Contain("at most");

        cut.Find("input[aria-label='Name of U_JURY']").Input("Eva");
        cut.Find("li.reviewer form").Submit();
        Runtime.NameOf("U_JURY").Should().Be("Eva");
        cut.Find(".reviewer-name").TextContent.Should().Be("Eva");
        cut.Find(".reviewer-status").TextContent.Should().Contain("never replaced by one from Slack");

        cut.Find("button.edit-name").Click();
        cut.Find("input[aria-label='Name of U_JURY']").GetAttribute("value").Should().Be("Eva");
        cut.Find("button[aria-label='Cancel']").Click();
        cut.FindAll("li.reviewer form").Should().BeEmpty();
    }

    [Fact]
    public void FetchNames_ShouldReportWhatHappened()
    {
        ConnectSlack();
        Runtime.AddReviewer("U_PIET");
        Runtime.AddReviewer("U_KAREL");
        Runtime.SetReviewerName("U_KAREL", "Karel");
        _slack.Setup(s => s.GetUserNameAsync("U_JURY", It.IsAny<CancellationToken>())).ReturnsAsync(SlackUserLookup.Found("Eva"));
        var cut = Render<ReviewerManager>();

        cut.Find("button.fetch-names").Click();

        cut.WaitForAssertion(() => cut.Find(".reviewer-status .text-danger").TextContent
            .Should().Contain("1 name(s) fetched from Slack, 1 kept as entered.").And.Contain("Slack does not know U_PIET"));
        Runtime.NameOf("U_JURY").Should().Be("Eva");
    }

    [Fact]
    public void FetchNames_WithoutProblems_ShouldConfirm()
    {
        ConnectSlack();
        _slack.Setup(s => s.GetUserNameAsync("U_JURY", It.IsAny<CancellationToken>())).ReturnsAsync(SlackUserLookup.Found("Eva"));
        var cut = Render<ReviewerManager>();

        cut.Find("button.fetch-names").Click();

        cut.WaitForAssertion(() => cut.Find(".reviewer-status .text-success").TextContent.Should().Be("1 name(s) fetched from Slack."));
    }

    [Fact]
    public void Remove_ShouldTakeReviewerAway()
    {
        Runtime.AddReviewer("U_ADDED");
        var cut = Render<ReviewerManager>();

        cut.Find("button.remove-reviewer").Click();

        Runtime.IsReviewer("U_ADDED").Should().BeFalse();
        Rows(cut).Should().ContainSingle();
    }

    [Fact]
    public void NoReviewersAtAll_ShouldWarn()
    {
        Settings.Reviewers = new List<string>();

        Render<ReviewerManager>().Markup.Should().Contain("No reviewers: all reactions are ignored.");
    }

    [Fact]
    public void NonAdmin_ShouldOnlySeeTheList()
    {
        ConnectSlack();
        Runtime.AddReviewer("U_ADDED");
        LogInAsJury();

        var cut = Render<ReviewerManager>();

        Rows(cut).Should().HaveCount(2);
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll("input").Should().BeEmpty();
        cut.Markup.Should().Contain("Only admins can add reviewers");
    }

    [Fact]
    public async Task ChangesElsewhere_ShouldShowAndStopAfterClose()
    {
        var cut = Render<ReviewerManager>();

        Runtime.AddReviewer("U_ELSEWHERE");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("U_ELSEWHERE"));

        await DisposeComponentsAsync();
        var act = () => Runtime.AddReviewer("U_LATER");
        act.Should().NotThrow();
    }
}
