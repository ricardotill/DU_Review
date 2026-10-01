using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Services;
using ScoutingAutomation.Web.Components.Pages;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

public class EventsPageTests : PageTestBase
{
    private SlackEventLog Log => Services.GetRequiredService<SlackEventLog>();

    private static SlackEventLogEntry Entry(SlackEventKind kind, SlackEventOutcome outcome, string channel, string detail, params Guid[] ids) => new(
        Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(12), kind, channel, "U_JURY", "Opdracht 1", null, 1,
        outcome, detail, ids);

    private static IReadOnlyList<string> Rows(IRenderedComponent<Events> cut) =>
        cut.FindAll("tbody tr").Select(r => r.TextContent).ToList();

    [Fact]
    public void Empty_ShouldSayNothingHappenedYet()
    {
        var cut = Render<Events>();

        cut.Markup.Should().Contain("Nog geen gebeurtenissen");
    }

    [Fact]
    public void Events_ShouldShowNewestFirstWithGroupAndOutcome()
    {
        Seed();
        var id = Guid.NewGuid();
        Log.Add(Entry(SlackEventKind.Message, SlackEventOutcome.Ignored, "C1", "Geen opdrachtnummer gevonden"));
        Log.Add(Entry(SlackEventKind.Message, SlackEventOutcome.Processed, "C2", "1 inzending(en) geregistreerd", id));

        var cut = Render<Events>();

        cut.WaitForAssertion(() => Rows(cut)[0].Should().Contain("De Bosuilen"));
        var rows = Rows(cut);
        rows[0].Should().Contain("Verwerkt").And.Contain("1 inzending(en) geregistreerd");
        rows[1].Should().Contain("De Wolven").And.Contain("Genegeerd").And.Contain("Geen opdrachtnummer gevonden");
        cut.Find($"a[href='submissions/{id}']").Should().NotBeNull();
    }

    [Fact]
    public void NewEvent_ShouldAppearLive()
    {
        var cut = Render<Events>();

        Log.Add(Entry(SlackEventKind.ReactionAdded, SlackEventOutcome.Failed, "C9", "boom"));

        cut.WaitForAssertion(() => Rows(cut).Should().ContainSingle().Which.Should().Contain("Mislukt").And.Contain("#C9"));
    }

    [Fact]
    public void Paused_ShouldHoldNewEventsUntilResumed()
    {
        var cut = Render<Events>();
        cut.Find("button.pause-toggle").Click();

        Log.Add(Entry(SlackEventKind.Deleted, SlackEventOutcome.Processed, "C1", "weg"));

        cut.WaitForAssertion(() => cut.Find("button.pause-toggle").TextContent.Should().Contain("1 nieuw"));
        Rows(cut).Should().BeEmpty();

        cut.Find("button.pause-toggle").Click();

        Rows(cut).Should().ContainSingle().Which.Should().Contain("Verwijderd");
    }

    [Fact]
    public void Filters_ShouldNarrowTheList()
    {
        Log.Add(Entry(SlackEventKind.Message, SlackEventOutcome.Processed, "C1", "a"));
        Log.Add(Entry(SlackEventKind.ReactionRemoved, SlackEventOutcome.Ignored, "C2", "b"));
        var cut = Render<Events>();

        cut.FindAll("select")[0].Change(nameof(SlackEventKind.ReactionRemoved));
        Rows(cut).Should().ContainSingle().Which.Should().Contain("Emoji weggehaald");

        cut.FindAll("select")[0].Change("");
        cut.FindAll("select")[1].Change(nameof(SlackEventOutcome.Processed));
        Rows(cut).Should().ContainSingle().Which.Should().Contain("Bericht");

        cut.FindAll("select")[1].Change(nameof(SlackEventOutcome.Failed));
        cut.Markup.Should().Contain("Geen gebeurtenissen met deze filters.");
    }

    [Fact]
    public void SimulatorUpload_ShouldShowUpOnTheEventsPage()
    {
        Seed();
        var events = Render<Events>();
        var simulator = Render<SlackSimulator>();
        simulator.WaitForAssertion(() => simulator.Markup.Should().Contain("Bestand uploaden"));

        simulator.Find("button.btn-scout-primary").Click();

        events.WaitForAssertion(() => Rows(events).Should().ContainSingle().Which.Should().Contain("Bericht"));
    }
}
