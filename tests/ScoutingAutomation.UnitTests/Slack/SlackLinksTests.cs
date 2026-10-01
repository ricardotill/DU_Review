using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackLinksTests
{
    private static readonly SlackWorkspace Workspace = new("T1", "https://scouting.slack.com/");

    [Fact]
    public void AppLink_ShouldOpenTheMessageInTheSlackApp() =>
        SlackLinks.AppLink(Workspace, "C1", "1712345678.000100").Should().Be("slack://channel?team=T1&id=C1&message=1712345678.000100");

    [Fact]
    public void WebLink_ShouldBeThePermalink() =>
        SlackLinks.WebLink(Workspace, "C1", "1712345678.000100").Should().Be("https://scouting.slack.com/archives/C1/p1712345678000100");

    [Theory]
    [InlineData(null, "1.2")]
    [InlineData("C1", null)]
    [InlineData("C1", " ")]
    public void Links_WithoutMessage_ShouldBeNull(string? channel, string? ts)
    {
        SlackLinks.AppLink(Workspace, channel, ts).Should().BeNull();
        SlackLinks.WebLink(Workspace, channel, ts).Should().BeNull();
    }

    [Fact]
    public async Task Links_WithoutWorkspace_ShouldBeNullAndAskSlackAgainLater()
    {
        var slack = new Mock<ISlackClient>();
        slack.Setup(s => s.IsConnected).Returns(true);
        slack.SetupSequence(s => s.GetWorkspaceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((SlackWorkspace?)null)
            .ReturnsAsync(Workspace);
        var time = new Clock { Now = DateTimeOffset.UtcNow };
        var links = new SlackLinks(new ServiceCollection().AddSingleton(slack.Object).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            time, NullLogger<SlackLinks>.Instance);

        await links.EnsureLoadedAsync();
        await links.EnsureLoadedAsync();
        links.AppLink("C1", "1.2").Should().BeNull();
        slack.Verify(s => s.GetWorkspaceAsync(It.IsAny<CancellationToken>()), Times.Once);

        time.Now += SlackLinks.RetryAfter;
        await links.EnsureLoadedAsync();
        links.AppLink("C1", "1.2").Should().NotBeNull();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
