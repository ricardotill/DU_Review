using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackSocketModeServiceTests
{
    private readonly ISlackSocketConnector _connector = Substitute.For<ISlackSocketConnector>();
    private readonly SlackConnectionStatus _status = new();

    private SlackSocketModeService Create(SlackSettings slack) => new(
        Options.Create(new ScoutingSettings { Slack = slack }),
        _connector,
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        _status,
        Substitute.For<ISlackCatchUpService>(),
        NullLogger<SlackSocketModeService>.Instance);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NotInSocketMode_ShouldStayDisabled(bool simulation, bool socketMode)
    {
        var service = Create(new SlackSettings { SimulationMode = simulation, SocketMode = socketMode, AppToken = "xapp" });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        _status.State.Should().Be(SlackConnectionState.Disabled);
        _connector.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAppToken_ShouldReportError()
    {
        var service = Create(new SlackSettings { SimulationMode = false, SocketMode = true });

        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;

        _status.State.Should().Be(SlackConnectionState.Error);
        _status.Detail.Should().Be("App-token niet ingesteld");
    }

    [Fact]
    public async Task ConnectionFailure_ShouldReportErrorAndRetryUntilStopped()
    {
        var attempts = 0;
        _connector.OpenConnectionAsync("xapp", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Slack refused the Socket Mode connection: invalid_auth"))
            .AndDoes(_ => attempts++);
        var service = Create(new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = "xapp" });

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (attempts < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        attempts.Should().BeGreaterThanOrEqualTo(2, "the service retries after a failure");
        _status.State.Should().Be(SlackConnectionState.Error);
        _status.Detail.Should().Contain("invalid_auth");
        service.ExecuteTask!.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task StopWhileConnecting_ShouldEndCleanly()
    {
        _connector.OpenConnectionAsync("xapp", Arg.Any<CancellationToken>())
            .Returns(async ci => { await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(1)); return new Uri("ws://x"); });
        var service = Create(new SlackSettings { SimulationMode = false, SocketMode = true, AppToken = "xapp" });

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await service.StopAsync(CancellationToken.None);

        service.ExecuteTask!.IsCompletedSuccessfully.Should().BeTrue();
        _status.State.Should().Be(SlackConnectionState.Connecting);
    }
}
