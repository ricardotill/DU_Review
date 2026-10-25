using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Announcements;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Slack;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class AdventurersAnnouncerTests
{
    private static readonly Group Wolves = new() { Name = "De Wolven", SlackChannelId = "C1", AdventurersSlackChannelId = "CAV1", MapToken = "abc123" };

    [Fact]
    public void Render_ShouldFillTheGroupMapLinkAndTime()
    {
        var settings = new ScoutingSettings
        {
            PublicUrl = "https://du.example.nl/",
            ReviewStatusReactions = new ReviewStatusReactionsConfig { Approved = new() { "thumbsup" }, Rejected = new() { "x" } }
        };
        var end = new DateTimeOffset(2026, 10, 3, 14, 30, 0, TimeSpan.Zero);

        var text = AdventurersAnnouncer.Render("{group}: {map} om {time} {approved}/{rejected}", Wolves.Name,
            AdventurersLinks.MapUrl(settings, Wolves)!, settings, end);

        text.Should().Be($"De Wolven: https://du.example.nl/kaart/abc123 om {end.ToGameTime():HH:mm} :thumbsup:/:x:");
    }

    [Fact]
    public void MapUrl_WithoutPublicAddress_ShouldBeNull()
    {
        AdventurersLinks.MapUrl(new ScoutingSettings(), Wolves).Should().BeNull();
    }

    [Fact]
    public async Task GroupMessageSender_ShouldPostInTheChosenChannels()
    {
        var groups = Substitute.For<IGroupRepository>();
        groups.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Group> { Wolves, new() { Name = "Zonder", SlackChannelId = "C2" } });
        var slack = Substitute.For<ISlackClient>();
        var sender = new GroupMessageSender(groups, slack, NullLogger<GroupMessageSender>.Instance);
        var all = (await groups.GetAllAsync()).Select(g => g.Id).ToList();

        var adventurers = await sender.PostAsync("Hoi", all, channels: MessageChannels.Avonturiers);
        var both = await sender.PostAsync("Hoi", all, channels: MessageChannels.Both);

        adventurers.Select(r => (r.GroupName, r.ChannelId)).Should().Equal(("De Wolven (Avonturiers)", "CAV1"));
        both.Select(r => r.ChannelId).Should().Equal("C1", "CAV1", "C2");
    }
}
