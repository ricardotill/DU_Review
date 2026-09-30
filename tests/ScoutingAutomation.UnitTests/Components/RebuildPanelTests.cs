using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class RebuildPanelTests : BunitContext
{
    private readonly FakeRebuildService _service = new();

    public RebuildPanelTests()
    {
        Services.AddSingleton<ISlackRebuildService>(_service);
    }

    [Fact]
    public void Unavailable_ShouldExplainWhen()
    {
        _service.IsAvailable = false;

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Available when the app is connected to Slack");
        cut.FindAll("details").Should().BeEmpty();
    }

    [Fact]
    public void Start_ShouldRequireTypedConfirmationAndUseChosenTime()
    {
        var cut = Render<RebuildPanel>();
        var button = () => cut.Find("button");

        button().HasAttribute("disabled").Should().BeTrue();
        cut.Find("details").HasAttribute("open").Should().BeFalse();

        cut.Find("input[type=datetime-local]").Change("2026-09-30T08:30");
        cut.Find("input[type=text]").Input("rebuild");
        button().HasAttribute("disabled").Should().BeTrue("the confirmation is case-sensitive");

        cut.Find("input[type=text]").Input(" REBUILD ");
        button().Click();

        _service.StartedWith.Should().NotBeNull();
        _service.StartedWith!.Value.DateTime.Should().Be(new DateTime(2026, 9, 30, 8, 30, 0));
        _service.StartedWith.Value.Offset.Should().Be(TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 30, 8, 30, 0)));
        cut.Find("input[type=text]").GetAttribute("value").Should().BeEmpty();
    }

    [Fact]
    public void Running_ShouldShowLiveProgressAndLockInputs()
    {
        var cut = Render<RebuildPanel>();

        _service.Publish(new RebuildProgress { State = RebuildState.Running, Step = "Reading Slack", ChannelsRead = 1, ChannelsTotal = 4, MessagesFound = 30 });

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Reading Slack…"));
        cut.Find("details").HasAttribute("open").Should().BeTrue();
        cut.Find(".rebuild-stats").TextContent.Should().Contain("1 / 4").And.Contain("0 / 30");
        cut.FindAll("input").Should().OnlyContain(i => i.HasAttribute("disabled"));
        cut.Find("button").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".rebuild-stats dt").Select(d => d.TextContent).Should().NotContain("Backup");
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
            Conflicts = new[] { new RebuildIssue("De Wolven", at, "Reviewers disagree; Approved applied", "Opdracht 4") },
            Skipped = new[] { new RebuildIssue("De Bosuilen", at, "Unsupported file: a.zip", null) }
        });

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Rebuild finished").And.Contain("/data/_backup/20260930-120000");
        cut.Markup.Should().Contain("Reviewers disagreed (1)").And.Contain("Not imported (1)");
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
            Error = "1 channel(s) could not be read; nothing was changed",
            ChannelProblems = new[] { new RebuildChannelProblem("Wilo", "C07PF3EK20Z", "not_in_channel", "Invite the bot to this channel.") }
        });

        var cut = Render<RebuildPanel>();

        var problem = cut.Find("ul.channel-problems li");
        problem.TextContent.Should().Contain("Wilo").And.Contain("#C07PF3EK20Z").And.Contain("not_in_channel").And.Contain("Invite the bot");
        cut.Markup.Should().Contain("Channels that could not be read (1)");
    }

    [Fact]
    public void Failed_ShouldShowErrorEvenWhenNoLongerAvailable()
    {
        _service.IsAvailable = false;
        _service.Publish(new RebuildProgress { State = RebuildState.Failed, Error = "not_in_channel" });

        var cut = Render<RebuildPanel>();

        cut.Markup.Should().Contain("Rebuild failed: not_in_channel");
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
        public int Listeners => _changed?.GetInvocationList().Length ?? 0;

        public event Action? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        public bool TryStart(DateTimeOffset since)
        {
            StartedWith = since;
            Progress = new RebuildProgress { State = RebuildState.Running, Step = "Starting" };
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

        cut.Markup.Should().Contain("Download failed").And.Contain("kon niet worden opgehaald");
        cut.Markup.Should().Contain("Rebuild from Slack").And.Contain("Available when the app is connected to Slack");
    }
}
