using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SlackTextEditorTests : PageTestBase
{
    private RuntimeSettingsService Runtime => Services.GetRequiredService<RuntimeSettingsService>();

    private readonly BunitJSModuleInterop _composer;

    public SlackTextEditorTests()
    {
        _composer = JSInterop.SetupModule("./js/slack-composer.js");
        _composer.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Types in the editor: the browser reports the new text, and the editor reads it back on save.</summary>
    private void Type(IRenderedComponent<SlackTextEditor> cut, string mrkdwn)
    {
        _composer.Setup<string?>("toMrkdwn", _ => true).SetResult(mrkdwn);
        cut.InvokeAsync(() => cut.FindComponent<SlackComposer>().Instance.OnContentChanged(mrkdwn));
    }

    private IRenderedComponent<SlackTextEditor> RenderEditor() =>
        Render<SlackTextEditor>(p => p.Add(e => e.Kind, SlackText.DownloadFailed).Add(e => e.Label, "Download mislukt"));

    [Fact]
    public void Edit_ShouldSaveTheTextAndOfferAReset()
    {
        var cut = RenderEditor();
        cut.Find(".slack-text").TextContent.Should().Contain("kon niet worden opgehaald");

        cut.Find("button.edit-text").Click();
        Type(cut, "*Mislukt*: {files} :warning:");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Runtime.Text(SlackText.DownloadFailed).Should().Be("*Mislukt*: {files} :warning:"));
        cut.Find(".slack-text").TextContent.Should().Be("*Mislukt*: {files} :warning:");

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

    [Fact]
    public void Edit_ShouldOpenTheEditorWithTheCurrentText()
    {
        var cut = RenderEditor();

        cut.Find("button.edit-text").Click();

        cut.FindComponent<SlackComposer>().Instance.Value.Should().Be(Runtime.Text(SlackText.DownloadFailed));
        _composer.VerifyInvoke("init").Arguments.Last().Should().Be(Runtime.Text(SlackText.DownloadFailed));
    }

    [Fact]
    public void SaveUntouched_ShouldKeepTheTextExactlyAsItWas()
    {
        var before = Runtime.Text(SlackText.DownloadFailed);
        // The editor would write it differently; untouched, it is not asked
        _composer.Setup<string?>("toMrkdwn", _ => true).SetResult("herschreven");
        var cut = RenderEditor();

        cut.Find("button.edit-text").Click();
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.FindAll("form").Should().BeEmpty());
        Runtime.Text(SlackText.DownloadFailed).Should().Be(before);
    }

    [Fact]
    public void TooLongText_ShouldNotBeSaved()
    {
        var cut = RenderEditor();
        cut.Find("button.edit-text").Click();

        Type(cut, new string('a', RuntimeSettingsService.MaxSlackTextLength + 1));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("maximaal"));
        Runtime.IsTextChanged(SlackText.DownloadFailed).Should().BeFalse();
    }
}
