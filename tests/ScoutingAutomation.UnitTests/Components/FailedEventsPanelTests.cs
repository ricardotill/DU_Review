using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class FailedEventsPanelTests : BunitContext
{
    private readonly Mock<IFailedSlackEventRepository> _failures = new();
    private readonly Mock<ISlackCatchUpService> _catchUp = new();
    private readonly SlackEventLog _log = new(NullLogger<SlackEventLog>.Instance);
    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public FailedEventsPanelTests()
    {
        _failures.Setup(f => f.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[]
        {
            new FailedSlackEvent { Id = Guid.NewGuid(), Kind = nameof(SlackEventKind.Message), ChannelId = "C01", Error = "download mislukt", Attempts = 1 }
        });
        Services.AddSingleton(_failures.Object);
        Services.AddSingleton(_catchUp.Object);
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
        _failures.Setup(f => f.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<FailedSlackEvent>());
        Render<FailedEventsPanel>();

        _log.AddNotice(SlackEventKind.Message, SlackEventOutcome.Ignored, "Geen bestanden in het bericht");

        _failures.Verify(f => f.GetPendingAsync(It.IsAny<CancellationToken>()), Times.Once, "only the first load");
    }

    [Fact]
    public async Task BurstOfFailures_ShouldReloadOnce()
    {
        var cut = Render<FailedEventsPanel>();

        for (var i = 0; i < 20; i++) _log.AddNotice(SlackEventKind.Message, SlackEventOutcome.Failed, "download mislukt");
        await Task.Delay(FailedEventsPanel.ReloadInterval * 2);

        cut.WaitForAssertion(() =>
            _failures.Verify(f => f.GetPendingAsync(It.IsAny<CancellationToken>()), Times.Exactly(2), "the first load and one for the burst"));
    }

    [Fact]
    public void CatchUp_ByAnAdmin_ShouldBeRequested()
    {
        _auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
        var cut = Render<FailedEventsPanel>();

        cut.Find(".run-catch-up").Click();

        _catchUp.Verify(c => c.Request(It.IsAny<string>()), Times.Once);
    }
}
