using Bunit;
using Microsoft.AspNetCore.Components.Web;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Enums;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Web.Components.Pages;
using ScoutingAutomation.Web.Components.Scores;
using ScoutingAutomation.Web.Components.Shared;
using Xunit;

namespace ScoutingAutomation.UnitTests.Components;

/// <summary>Last activity per group, the points-over-time chart, the beamer scoreboard and the scoreboard in the slideshow.</summary>
public class ScoreboardPagesTests : PageTestBase
{
    private static DateTime GameNow => DateTimeOffset.UtcNow.ToGameTime().DateTime;

    private Domain.Models.SubmissionStatistics Statistics()
    {
        using var scope = Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISubmissionRepository>().GetStatisticsAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void Statistics_ShouldGiveLastUploadAndWhenPointsWereEarned()
    {
        var seeded = Seed();

        var stats = Statistics();

        var byGroup = stats.GroupStatistics.ToDictionary(g => g.GroupId);
        byGroup[Wolves.Id].LastSubmissionAt.Should().Be(seeded.Approved.CreatedAt);
        byGroup[Owls.Id].LastSubmissionAt.Should().Be(seeded.Unresolved.CreatedAt, "an upload without a number is activity too");
        byGroup[Foxes.Id].LastSubmissionAt.Should().BeNull();

        // #2 awesome (4) first, then #1 approved (1) at the time of the approved resubmission, not the rejected first try
        stats.ScoreEvents.Should().Equal(
            new Domain.Models.ScoreEvent(Wolves.Id, "2", seeded.Awesome.CreatedAt, 4),
            new Domain.Models.ScoreEvent(Wolves.Id, "1", seeded.Approved.CreatedAt, 1));
    }

    [Fact]
    public void Dashboard_ShouldWarnAboutQuietGroupsDuringTheGame()
    {
        Seed();
        Settings.EventStart = GameNow.AddHours(-2);

        var cut = Render<Home>();

        cut.WaitForAssertion(() => cut.FindAll(".leaderboard tbody tr").Should().HaveCount(3));
        var activity = cut.FindAll(".leaderboard tbody tr").ToDictionary(
            r => r.QuerySelector("a")!.TextContent,
            r => r.QuerySelector(".last-activity")!);
        activity["De Wolven"].ClassList.Should().Contain("text-danger", "the last upload was an hour ago");
        activity["De Wolven"].TextContent.Should().Contain("1 u 00 min geleden");
        activity["De Bosuilen"].ClassList.Should().Contain("text-white-50");
        activity["De Bosuilen"].TextContent.Should().Contain("10 min geleden");
        activity["De Vossen"].ClassList.Should().Contain("text-danger", "nothing since the start two hours ago");
        activity["De Vossen"].TextContent.Should().Contain("Nog niets");
    }

    [Fact]
    public void LastActivity_ShouldNotWarnBeforeTheStartOrAfterTheEnd()
    {
        var longAgo = DateTimeOffset.UtcNow.AddHours(-3);

        Settings.EventStart = GameNow.AddHours(1);
        Render<LastActivity>(p => p.Add(c => c.At, longAgo)).Find(".last-activity").ClassList.Should().Contain("text-white-50");

        Settings.EventStart = GameNow.AddHours(-5);
        Settings.EventEnd = GameNow.AddHours(-1);
        Render<LastActivity>(p => p.Add(c => c.At, longAgo)).Find(".last-activity").ClassList.Should().Contain("text-white-50");
    }

    [Fact]
    public void ScoreChart_ShouldDrawALinePerGroupWithTheTotalsInTheLegend()
    {
        Seed();

        var cut = Render<ScoreChart>(p => p.Add(c => c.Stats, Statistics()));

        cut.FindAll("path.line").Should().HaveCount(3, "every group gets a line, also without points");
        cut.FindAll(".legend-item").Select(i => i.TextContent.Trim()).Should().Equal("De Wolven 5", "De Bosuilen 0", "De Vossen 0");
        cut.FindAll("circle.hit title").Select(t => t.TextContent).Should().ContainSingle(t => t.Contains("opdracht 2 +4 → 4 punten"));
        cut.FindAll("details tbody tr").Should().HaveCount(2);
    }

    [Fact]
    public void ScoreChart_PointsTakenAway_ShouldShowAStepDownAndKeepThePeakOnTheAxis()
    {
        var group = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddHours(-2);
        var stats = new Domain.Models.SubmissionStatistics
        {
            GroupStatistics = { new Domain.Models.GroupStatistic { GroupId = group, GroupName = "De Wolven", Score = 6 } },
            ScoreEvents =
            {
                new Domain.Models.ScoreEvent(group, "", start, 10, "Avonturiers"),
                new Domain.Models.ScoreEvent(group, "", start.AddHours(1), -4, "Avonturiers")
            }
        };

        var cut = Render<ScoreChart>(p => p.Add(c => c.Stats, stats));

        cut.FindAll("circle.hit title").Select(t => t.TextContent).Should().Contain(t => t.Contains("Avonturiers \u22124 → 6 punten"));
        cut.FindAll("details tbody tr").Last().TextContent.Should().Contain("\u22124");
        cut.FindAll("text.axis[text-anchor=end]").Select(t => t.TextContent).Should().Contain("10", "the line reached 10 before points were taken away");
        cut.FindAll(".legend-item").Select(i => i.TextContent.Trim()).Should().Equal("De Wolven 6");
    }

    [Fact]
    public void ScoreChart_WithHighlight_ShouldShowOneGroupInColorAndTheOthersAsContext()
    {
        Seed();

        var cut = Render<ScoreChart>(p => p
            .Add(c => c.Stats, Statistics())
            .Add(c => c.Highlight, Owls.Id));

        cut.FindAll("path.line:not(.context)").Should().ContainSingle();
        cut.FindAll("path.line.context").Should().HaveCount(2);
        cut.FindAll("circle.hit").Should().BeEmpty("the Owls have no points; the Wolves' points are context only");
        cut.FindAll(".legend-item").Select(i => i.TextContent.Trim()).Should().Equal("De Bosuilen 0", "Andere groepen");
        cut.FindAll(".end-label").Select(l => l.TextContent).Should().Equal("De Bosuilen");
        cut.Find("svg").GetAttribute("aria-label").Should().Contain("De Bosuilen");
    }

    [Fact]
    public void GroupPage_ShouldShowThePointsOfThatGroup()
    {
        Seed();

        var cut = Render<GroupDetail>(p => p.Add(c => c.Id, Wolves.Id));

        cut.WaitForAssertion(() => cut.FindAll(".score-chart").Should().ContainSingle());
        cut.FindAll(".legend-item").Select(i => i.TextContent.Trim()).Should().Equal("De Wolven 5", "Andere groepen");
        cut.FindAll(".score-chart details tbody tr").Should().HaveCount(2);
    }

    [Fact]
    public void ScoreChart_WithoutPoints_ShouldShowEmptyState()
    {
        var cut = Render<ScoreChart>(p => p.Add(c => c.Stats, Statistics()));

        cut.Markup.Should().Contain("Nog geen punten.");
        cut.FindAll("svg").Should().BeEmpty();
    }

    [Fact]
    public void ScoreboardPage_ShouldListGroupsByRankAndUpdateLive()
    {
        Seed();
        var cut = Render<Scoreboard>();

        cut.WaitForAssertion(() => cut.FindAll(".board-row").Should().HaveCount(3));
        cut.FindAll(".board-name").Select(n => n.TextContent).Should().Equal("De Wolven", "De Bosuilen", "De Vossen");
        cut.Find(".board-row.gold .board-score").TextContent.Should().StartWith("5");

        WithDb(db =>
        {
            db.Submissions.Add(NewSubmission(Foxes, 3, SubmissionStatus.Awesome, "nieuw.jpg", DateTimeOffset.UtcNow, "image/jpeg", 1));
            db.SaveChanges();
        });
        NotifyChanged();

        cut.WaitForAssertion(() => cut.FindAll(".board-name")[1].TextContent.Should().Be("De Vossen"), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Slideshow_ShouldShowTheScoreboardBetweenItems()
    {
        Seed();
        var cut = Render<Slideshow>();
        cut.WaitForAssertion(() => cut.Find(".item-count").TextContent.Should().Be("3 items"));
        cut.Find("#slideshowScoreboard").Change("3");
        cut.FindAll("button").First(b => b.TextContent.Contains("Diavoorstelling starten")).Click();

        // Awaited: on a busy machine an event that is only queued would show its result after the check
        await cut.Find(".media video").TriggerEventAsync("onended", EventArgs.Empty);
        await cut.Find("button[aria-label=Volgende]").ClickAsync(new MouseEventArgs());
        await cut.Find(".media audio").TriggerEventAsync("onended", EventArgs.Empty);

        cut.Find(".scoreboard-slide").TextContent.Should().Contain("Tussenstand").And.Contain("De Wolven");
        cut.FindAll(".caption").Should().BeEmpty();

        await cut.Find("button[aria-label=Volgende]").ClickAsync(new MouseEventArgs());

        cut.FindAll(".scoreboard-slide").Should().BeEmpty();
        cut.Find(".position").TextContent.Should().Be("1 / 3", "after the last item the show loops to the first");
    }
}
