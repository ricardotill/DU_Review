using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class RebuildPanelTests : BunitContext
{
    private readonly FakeRebuildService _service = new();
    private readonly ScoutingSettings _settings = new();

    private readonly Bunit.TestDoubles.BunitAuthorizationContext _auth;

    public RebuildPanelTests()
    {
        Services.AddSingleton<ISlackRebuildService>(_service);
        Services.AddSingleton(_ => TestRuntime.For(Options.Create(_settings)));
        // The panel is only shown to admins, and starting a rebuild is checked on the server too
        _auth = AddAuthorization();
        _auth.SetAuthorized("Admin");
        _auth.SetRoles(ScoutingAutomation.Web.Auth.KeyAuth.AdminRole);
    }

    [Fact]
    public void Start_ByATabThatIsNoLongerAdmin_ShouldNotRebuild()
    {
        var cut = Render<RebuildPanel>();
        cut.Find("input[type=text]").Input("OPBOUWEN");

        // E.g. the admin key was regenerated while the tab was open; the button is still on screen
        _auth.SetRoles();
        cut.Find("button").Click();

        _service.StartedWith.Should().BeNull();
        cut.Find(".start-error").TextContent.Should().Contain("beheermodus");
    }

    [Fact]
    public void Start_WithATimeOutsideTheAllowedYears_ShouldExplainInsteadOfFailing()
    {
        var cut = Render<RebuildPanel>();
        cut.Find("input[type=datetime-local]").Change("0001-01-01T00:00:00");
        cut.Find("input[type=text]").Input("OPBOUWEN");

        cut.Find("button").Click();

        _service.StartedWith.Should().BeNull();
        cut.Find(".start-error").TextContent.Should().Contain("2000");
    }

    [Fact]
    public void SinceTime_ShouldDefaultToEventStartWhenLaterThanMidnight()
    {
        // Tomorrow 09:00 is always after today's midnight
        var start = DateTime.Today.AddDays(1).AddHours(9);
        _settings.EventStart = start;

        var cut = Render<RebuildPanel>();

        cut.Find("input[type=datetime-local]").GetAttribute("value").Should().StartWith(start.ToString("yyyy-MM-ddTHH:mm"));
    }

    [Fact]
    public void Unavailable_ShouldExplainWhen()
    {
        _service.IsAvailable = false;

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Beschikbaar als de app met Slack is verbonden");
        cut.FindAll("details").Should().BeEmpty();
    }

    [Fact]
    public void Start_ShouldRequireTypedConfirmationAndUseChosenTime()
    {
        var cut = Render<RebuildPanel>();
        var button = () => cut.Find("button");

        button().HasAttribute("disabled").Should().BeTrue();
        cut.Find("details").HasAttribute("open").Should().BeFalse();

        cut.Find("input[type=datetime-local]").Change("2026-09-30T08:30:00");
        cut.Find("input[type=text]").Input("rebuild");
        button().HasAttribute("disabled").Should().BeTrue("the confirmation is case-sensitive");

        cut.Find("input[type=text]").Input(" OPBOUWEN ");
        button().Click();

        _service.StartedWith.Should().NotBeNull();
        _service.StartedForGroup.Should().BeNull("without a group, all groups are rebuilt");
        _service.StartedWith!.Value.DateTime.Should().Be(new DateTime(2026, 9, 30, 8, 30, 0));
        _service.StartedWith.Value.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 30, 8, 30, 0)));
        cut.Find("input[type=text]").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public void GroupPanel_ShouldRebuildOnlyThatGroup()
    {
        var group = new ScoutingAutomation.Domain.Entities.Group { Name = "De Wolven", SlackChannelId = "C1" };

        var cut = Render<RebuildPanel>(p => p.Add(c => c.Group, group));

        cut.Find("summary").TextContent.Should().Contain("van De Wolven");
        cut.Markup.Should().Contain("Andere groepen blijven ongemoeid").And.NotContain("Alle groepsmappen");

        cut.Find("input[type=text]").Input("OPBOUWEN");
        cut.Find("button").Click();

        _service.StartedForGroup.Should().Be(group.Id);
    }

    [Fact]
    public void Progress_ShouldNameTheRebuiltGroup()
    {
        _service.Publish(new RebuildProgress { State = RebuildState.Running, Step = "Importeren", GroupName = "De Wolven" });

        var cut = Render<RebuildPanel>();

        cut.Find(".rebuild-stats").TextContent.Should().Contain("De Wolven").And.NotContain("Alle groepen");
    }

    [Fact]
    public void Running_ShouldShowLiveProgressAndLockInputs()
    {
        var cut = Render<RebuildPanel>();

        _service.Publish(new RebuildProgress { State = RebuildState.Running, Step = "Slack lezen", ChannelsRead = 1, ChannelsTotal = 4, MessagesFound = 30 });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Slack lezen…"));
        cut.Find("details").HasAttribute("open").Should().BeTrue();
        cut.Find(".rebuild-stats").TextContent.Should().Contain("1 / 4").And.Contain("0 / 30").And.Contain("Alle groepen");
        cut.FindAll("input").Should().OnlyContain(i => i.HasAttribute("disabled"));
        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".rebuild-stats dt").Select(d => d.TextContent).Should().NotContain("Back-up");
    }

    [Fact]
    public void Completed_ShouldShowReport()
    {
        var at = new DateTimeOffset(2026, 9, 30, 10, 15, 0, TimeSpan.Zero);
        _service.Publish(new RebuildProgress
        {
            State = RebuildState.Completed,
            SubmissionsImported = 12,
            ReviewsApplied = 9,
            BackupFolder = "/data/_backup/20260930-120000",
            Conflicts = new[] { new RebuildIssue("De Wolven", at, "Juryleden zijn het oneens; Goedgekeurd toegepast", "Opdracht 4") },
            Skipped = new[] { new RebuildIssue("De Bosuilen", at, "Unsupported file: a.zip", null) }
        });

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Opnieuw opbouwen klaar").And.Contain("/data/_backup/20260930-120000");
        cut.Markup.Should().Contain("Juryleden waren het oneens (1)").And.Contain("Niet geïmporteerd (1)");
        var issues = cut.FindAll("ul.list-unstyled li");
        issues[0].TextContent.Should().Contain("De Wolven").And.Contain(at.ToLocalTime().ToString("HH:mm")).And.Contain("\"Opdracht 4\"");
        issues[1].TextContent.Should().Contain("Unsupported file: a.zip").And.NotContain("\"");
    }

    [Fact]
    public void FailedChannels_ShouldBeListedWithAdvice()
    {
        _service.Publish(new RebuildProgress
        {
            State = RebuildState.Failed,
            Error = "1 kanaal/kanalen konden niet worden gelezen; er is niets veranderd",
            ChannelProblems = new[] { new RebuildChannelProblem("Wilo", "C07PF3EK20Z", "not_in_channel", "Nodig de bot uit in dit kanaal.") }
        });

        var cut = Render<RebuildPanel>();

        var problem = cut.Find("ul.channel-problems li");
        problem.TextContent.Should().Contain("Wilo").And.Contain("#C07PF3EK20Z").And.Contain("not_in_channel").And.Contain("Nodig de bot uit");
        cut.Markup.Should().Contain("Kanalen die niet gelezen konden worden (1)");
    }

    [Fact]
    public void Failed_ShouldShowErrorEvenWhenNoLongerAvailable()
    {
        _service.IsAvailable = false;
        _service.Publish(new RebuildProgress { State = RebuildState.Failed, Error = "not_in_channel" });

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Opnieuw opbouwen mislukt: not_in_channel");
        cut.FindAll("ul.list-unstyled li").Should().BeEmpty();
    }

    [Fact]
    public async Task ClosingPanel_ShouldStopListening()
    {
        Render<RebuildPanel>();
        _service.Listeners.Should().Be(1);

        await DisposeComponentsAsync();

        _service.Listeners.Should().Be(0);
    }

    private sealed class FakeRebuildService : ISlackRebuildService
    {
        private Action? _changed;

        public RebuildProgress Progress { get; private set; } = new();
        public bool IsAvailable { get; set; } = true;
        public Task Completion => Task.CompletedTask;
        public DateTimeOffset? StartedWith { get; private set; }
        public Guid? StartedForGroup { get; private set; }
        public int Listeners => _changed?.GetInvocationList().Length ?? 0;

        public event Action? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public bool TryStart(DateTimeOffset since, Guid? groupId = null)
        {
            StartedWith = since;
            StartedForGroup = groupId;
            Progress = new RebuildProgress { State = RebuildState.Running, Step = "Starten" };
            return true;
        }

        public void Publish(RebuildProgress progress)
        {
            Progress = progress;
            _changed?.Invoke();
        }
    }
}

public class ConfigurationRebuildTests : PageTestBase
{
    [Fact]
    public void Configuration_ShouldListDownloadFeedbackAndOfferRebuild()
    {
        var cut = Render<Configuration>();

        cut.Markup.Should().Contain("Download mislukt").And.Contain("kon niet worden opgehaald");
        cut.Markup.Should().Contain("Opnieuw opbouwen uit Slack").And.Contain("Beschikbaar als de app met Slack is verbonden");
    }
}
