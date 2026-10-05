using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Slack.Events;
using ScoutingAutomation.Infrastructure.Slack.Sync;
using ScoutingAutomation.Web.Components.Panels;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class FailedEventsPanelTests : BunitContext
{
    private readonly IFailedSlackEventRepository _failures = Substitute.For<IFailedSlackEventRepository>();
    private readonly ISlackCatchUpService _catchUp = Substitute.For<ISlackCatchUpService>();
    private readonly SlackEventLog _log = new(NullLogger<SlackEventLog>.Instance);
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public FailedEventsPanelTests()
    {
        _failures.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
        {
            new FailedSlackEvent { Id = Guid.NewGuid(), Kind = nameof(SlackEventKind.Message), ChannelId = "C01", Error = "download mislukt", Attempts = 1 }
        });
        Services.AddSingleton(_failures);
        Services.AddSingleton(_catchUp);
        Services.AddSingleton(_log);
        Services.AddSingleton(_ => TestRuntime.For(Microsoft.Extensions.Options.Options.Create(new ScoutingAutomation.Contracts.Configuration.ScoutingSettings())));
        _auth = AddAuthorization();
        _auth.SetAuthorized("Jury");
    }

    [Fact]
    public void Jury_ShouldSeeFailures_ButNoButtons()
    {
        var cut = Render<FailedEventsPanel>(p => p.Add(x => x.GroupNames, new Dictionary<string, string> { ["C01"] = "De Zeearenden" }));

        cut.Find("tbody").TextContent.Should().Contain("De Zeearenden").And.Contain("download mislukt");
        cut.FindAll(".retry-one, .retry-all, .run-catch-up, .dismiss-one").Should().BeEmpty();
    }

    [Fact]
    public void IgnoredEvent_WithNothingShown_ShouldNotQueryTheDatabase()
    {
        _failures.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<FailedSlackEvent>());
        Render<FailedEventsPanel>();

        _log.AddNotice(SlackEventKind.Message, SlackEventOutcome.Ignored, "Geen bestanden in het bericht");

        _failures.Received(1).GetPendingAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BurstOfFailures_ShouldReloadOnce()
    {
        var cut = Render<FailedEventsPanel>();

        for (var i = 0; i < 20; i++) _log.AddNotice(SlackEventKind.Message, SlackEventOutcome.Failed, "download mislukt");
        await Task.Delay(FailedEventsPanel.ReloadInterval * 2);

        cut.WaitForAssertion(() =>
            _failures.Received(2).GetPendingAsync(Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void CatchUp_ByAnAdmin_ShouldBeRequested()
    {
        _auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
        var cut = Render<FailedEventsPanel>();

        cut.Find(".run-catch-up").Click();

        _catchUp.Received(1).Request(Arg.Any<string>());
    }
}
