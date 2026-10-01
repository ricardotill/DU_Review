using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SlackTextEditorTests : PageTestBase
{
    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    private IRenderedComponent<SlackTextEditor> RenderEditor() =>
        Render<SlackTextEditor>(p => p.Add(e => e.Kind, SlackText.DownloadFailed).Add(e => e.Label, "Download mislukt"));

    [Fact]
    public void Edit_ShouldSaveTheTextAndOfferAReset()
    {
        var cut = RenderEditor();
        cut.Find(".slack-text").TextContent.Should().Contain("kon niet worden opgehaald");

        cut.Find("button.edit-text").Click();
        cut.Find("textarea").Input("Mislukt: {files}");
        cut.Find("form").Submit();

        Runtime.Text(SlackText.DownloadFailed).Should().Be("Mislukt: {files}");
        cut.Find(".slack-text").TextContent.Should().Be("Mislukt: {files}");

        cut.Find("button.reset-text").Click();

        Runtime.IsTextChanged(SlackText.DownloadFailed).Should().BeFalse();
        cut.FindAll("button.reset-text").Should().BeEmpty();
    }

    [Fact]
    public void Jury_ShouldSeeTheTextButNotEditIt()
    {
        LogInAsJury();

        var cut = RenderEditor();

        cut.Find(".slack-text").TextContent.Should().NotBeEmpty();
        cut.FindAll("button.edit-text").Should().BeEmpty();
    }
}
