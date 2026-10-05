using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ScoutingAutomation.Infrastructure.Slack;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class MessagesPageTests : PageTestBase
{
    private readonly ISlackClient _slack = Substitute.For<ISlackClient>();
    private readonly List<(string Channel, string Text)> _posted = new();
    private readonly BunitJSModuleInterop _module;
    private static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };

    public MessagesPageTests()
    {
        _slack.IsConnected.Returns(true);
        _slack.PostMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null)
            .AndDoes(ci => _posted.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1))));
        Services.AddSingleton(_slack);

        _module = JSInterop.SetupModule("./js/slack-composer.js");
        _module.Mode = JSRuntimeMode.Loose;
        _module.Setup<string?>("toMrkdwn", _ => true).SetResult("*Lunch* om 12:00 🍕");
    }

    private IRenderedComponent<Messages> RenderPage(Guid? group = null)
    {
        Seed();
        if (group != null) Services.GetRequiredService<NavigationManager>().NavigateTo($"messages?group={group}");
        var cut = Render<Messages>();
        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Should().HaveCount(3));
        return cut;
    }

    private static void Type(IRenderedComponent<Messages> cut) =>
        cut.InvokeAsync(() => cut.FindComponent<ScoutingAutomation.Web.Components.Messaging.SlackComposer>().Instance.OnContentChanged("*Lunch* om 12:00 🍕"));

    [Fact]
    public void AllGroups_ShouldBeSelectedAtFirst()
    {
        var cut = RenderPage();

        cut.FindAll("input[type=checkbox]").Should().OnlyContain(c => c.HasAttribute("checked"));
        cut.Markup.Should().Contain("3 van 3 groepen").And.Contain("Sturen naar 3 kanalen");
        cut.Find("button.send-message").HasAttribute("disabled").Should().BeTrue("nothing was typed yet");
    }

    [Fact]
    public void GroupQuery_ShouldSelectOnlyThatGroup()
    {
        var cut = RenderPage(Owls.Id);

        cut.Markup.Should().Contain("1 van 3 groepen").And.Contain("Sturen naar 1 kanaal");
        cut.Find($"#message-group-{Owls.Id}").HasAttribute("checked").Should().BeTrue();
        cut.Find($"#message-group-{Wolves.Id}").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void SelectNone_ShouldDisableSending()
    {
        var cut = RenderPage();
        Type(cut);
        cut.Find("button.send-message").HasAttribute("disabled").Should().BeFalse();

        cut.Find("button.select-none").Click();

        cut.Markup.Should().Contain("0 van 3 groepen");
        cut.Find("button.send-message").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Send_ShouldAskToConfirmThenPostInTheSelectedChannels()
    {
        var cut = RenderPage();
        Type(cut);
        cut.Find($"#message-group-{Foxes.Id}").Change(false);

        cut.Find("button.send-message").Click();
        _posted.Should().BeEmpty("the first click only asks to confirm");
        cut.Find("button.send-message").TextContent.Should().Contain("Zeker? Sturen naar 2 kanalen");

        cut.Find("button.send-message").Click();

        cut.WaitForAssertion(() => _posted.Should().BeEquivalentTo(new[] { ("C1", "*Lunch* om 12:00 🍕"), ("C2", "*Lunch* om 12:00 🍕") }));
        cut.WaitForAssertion(() => cut.FindAll(".message-results li").Should().HaveCount(2));
        cut.Find(".message-results").TextContent.Should().Contain("De Wolven").And.Contain("Geplaatst");
    }

    [Fact]
    public void NotConnected_ShouldExplainAndNotSend()
    {
        _slack.IsConnected.Returns(false);
        var cut = RenderPage();
        Type(cut);

        cut.Markup.Should().Contain("Niet verbonden met Slack");
        cut.Find("button.send-message").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Jury_ShouldBeAbleToSendMessages()
    {
        LogInAsJury();
        var cut = RenderPage();

        cut.Find("button.send-message").Should().NotBeNull();
    }

    [Fact]
    public void Picture_ShouldBeSentEvenWithoutText()
    {
        var shared = new List<(string Channel, string FileName, int Bytes)>();
        _slack.PostFileAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((string?)null)
            .AndDoes(ci => shared.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(2), ci.ArgAt<byte[]>(1).Length)));
        var cut = RenderPage(Owls.Id);

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Jpeg, "vuur.jpg", contentType: "image/jpeg"));

        cut.WaitForAssertion(() => cut.Find(".message-picture").TextContent.Should().Contain("vuur.jpg"));
        cut.Find("button.send-message").Click();
        cut.Find("button.send-message").Click();

        cut.WaitForAssertion(() => shared.Should().Equal(("C2", "vuur.jpg", Jpeg.Length)));
        cut.WaitForAssertion(() => cut.FindAll(".message-picture").Should().BeEmpty("a sent picture is cleared"));
    }

    [Fact]
    public void NonImage_ShouldBeRefused()
    {
        var cut = RenderPage();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hoi", "notitie.txt", contentType: "text/plain"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Kies een afbeelding."));
        cut.FindAll(".message-picture").Should().BeEmpty();
    }

    [Fact]
    public void Picture_CanBeRemoved()
    {
        var cut = RenderPage();
        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromBinary(Jpeg, "vuur.jpg", contentType: "image/jpeg"));
        cut.WaitForAssertion(() => cut.FindAll(".message-picture").Should().HaveCount(1));

        cut.Find("button.remove-picture").Click();

        cut.FindAll(".message-picture").Should().BeEmpty();
        cut.Find("button.send-message").HasAttribute("disabled").Should().BeTrue("there is no text and no picture");
    }

    [Fact]
    public void FakePicture_ShouldBeRefusedEvenWithAnImageContentType()
    {
        var cut = RenderPage();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("<svg onload=alert(1)>", "vuur.jpg", contentType: "image/jpeg"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Kies een afbeelding."));
        cut.FindAll(".message-picture").Should().BeEmpty();
    }

    [Fact]
    public void SendAgain_AfterAFailure_ShouldOnlyRetryTheChannelsThatFailed()
    {
        var failing = true;
        // The constructor's AndDoes still records every post
        _slack.PostMessageAsync("C2", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => failing ? "not_in_channel" : null);
        var cut = RenderPage();
        Type(cut);

        cut.Find("button.send-message").Click();
        cut.Find("button.send-message").Click();
        cut.WaitForAssertion(() => cut.Find(".message-results").TextContent.Should().Contain("not_in_channel"));
        _posted.Select(p => p.Channel).Should().BeEquivalentTo("C1", "C2", "C3");

        cut.Markup.Should().Contain("1 van 3 groepen", "the channels that have the message are no longer chosen");
        failing = false;
        _posted.Clear();
        cut.Find("button.send-message").Click();
        cut.Find("button.send-message").Click();

        cut.WaitForAssertion(() => _posted.Select(p => p.Channel).Should().Equal("C2"));
    }

    [Fact]
    public void Sending_ShouldBeBusyFromTheFirstClick()
    {
        var cut = RenderPage();
        Type(cut);
        // The editor answers late, as over a slow connection
        var reading = _module.Setup<string?>("toMrkdwn", _ => true);

        cut.Find("button.send-message").Click();
        cut.Find("button.send-message").Click();

        cut.FindAll("button.send-message").Should().OnlyContain(b => b.HasAttribute("disabled"),
            "a second click while the text is read must not send the message again");
        _posted.Should().BeEmpty();

        reading.SetResult("Hoi");
        cut.WaitForAssertion(() => _posted.Should().HaveCount(3));
    }
}
