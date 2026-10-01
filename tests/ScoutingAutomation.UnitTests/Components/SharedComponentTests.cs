using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class SharedComponentTests : BunitContext
{
    [Theory]
    [InlineData(SubmissionStatus.Pending, "badge-pending", "bi-hourglass-split")]
    [InlineData(SubmissionStatus.Approved, "badge-approved", "bi-check2")]
    [InlineData(SubmissionStatus.Awesome, "badge-awesome", "bi-star-fill")]
    [InlineData(SubmissionStatus.Rejected, "badge-rejected", "bi-x-circle")]
    [InlineData(SubmissionStatus.Received, "badge-received", "bi-inbox")]
    public void StatusBadge_ShouldShowStatusWithMatchingStyle(SubmissionStatus status, string badge, string icon)
    {
        var cut = Render<StatusBadge>(p => p.Add(c => c.Status, status));

        cut.Find("span").ClassList.Should().Contain(badge);
        cut.Find("i").ClassList.Should().Contain(icon);
        cut.Markup.Should().Contain(status.StatusLabel());
    }

    [Theory]
    [InlineData("image/png", "bi-image")]
    [InlineData("video/mp4", "bi-camera-video")]
    [InlineData("audio/mp4", "bi-music-note-beamed")]
    [InlineData("application/pdf", "bi-file-earmark-pdf")]
    [InlineData("application/vnd.openxmlformats-officedocument.wordprocessingml.document", "bi-file-earmark-word")]
    [InlineData("application/zip", "bi-file-earmark")]
    public void FileIcon_ShouldMatchContentType(string contentType, string icon)
    {
        var cut = Render<FileIcon>(p => p.Add(c => c.ContentType, contentType));

        cut.Find("i").ClassList.Should().Contain(icon);
    }

    [Fact]
    public void PageHeader_WithEverything_ShouldRenderAllParts()
    {
        var cut = Render<PageHeader>(p => p
            .Add(c => c.Title, "Titel")
            .Add(c => c.Subtitle, "Uitleg")
            .Add(c => c.BackHref, "terug")
            .Add(c => c.BackText, "Terug")
            .Add(c => c.Details, (RenderFragment)(b => b.AddMarkupContent(0, "<span id='details'>d</span>")))
            .Add(c => c.Actions, (RenderFragment)(b => b.AddMarkupContent(0, "<button id='action'>a</button>"))));

        cut.Find("h1").TextContent.Should().Be("Titel");
        cut.Find("p").TextContent.Should().Be("Uitleg");
        cut.Find("a.back-link").GetAttribute("href").Should().Be("terug");
        cut.Find("#details");
        cut.Find("#action");
    }

    [Fact]
    public void PageHeader_WithTitleOnly_ShouldRenderNothingElse()
    {
        var cut = Render<PageHeader>(p => p.Add(c => c.Title, "Titel"));

        cut.FindAll("a").Should().BeEmpty();
        cut.FindAll("p").Should().BeEmpty();
        cut.FindAll("button").Should().BeEmpty();
        cut.Find("a, h1").TextContent.Should().Be("Titel");
    }

    [Fact]
    public void NotFound_ShouldLinkToDashboard()
    {
        var cut = Render<NotFound>();

        cut.Find("h1").TextContent.Should().Be("Pagina niet gevonden");
        cut.Find("a").GetAttribute("href").Should().Be("");
    }

    [Fact]
    public void Error_WithActivity_ShouldShowRequestId()
    {
        using var activity = new System.Diagnostics.Activity("test").Start();

        var cut = Render<Error>();

        cut.Markup.Should().Contain(activity.Id!);
    }

    [Fact]
    public void Error_WithoutActivity_ShouldUseTraceIdentifier()
    {
        System.Diagnostics.Activity.Current = null;
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { TraceIdentifier = "trace-42" };

        var cut = Render<Error>(p => p.AddCascadingValue(context));

        cut.Markup.Should().Contain("trace-42");
    }

    [Fact]
    public void Error_WithoutRequestId_ShouldHideIt()
    {
        System.Diagnostics.Activity.Current = null;

        var cut = Render<Error>();

        cut.Markup.Should().NotContain("Verzoek-ID");
    }
}
