using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Extensions;
using ScoutingAutomation.Infrastructure.Services;
using Xunit;

namespace ScoutingAutomation.UnitTests.Slack;

public class SlackClientRegistrationTests
{
    [Fact]
    public async Task SlackClient_ShouldNotUseTheAppWideResilienceHandler()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());
        services.AddScoutingInfrastructure(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Scouting:Slack:SimulationMode"] = "false", ["Scouting:Slack:BotToken"] = "xoxb-test" })
            .Build());
        services.AddHttpClient<ISlackClient, SlackClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<ISlackClient>().PostThreadReplyAsync("C1", "1.0", "Plaats opnieuw");

        handler.Requests.Should().HaveCount(1, "a retried post would show the reply twice in Slack");
        provider.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ISlackClient)).Timeout
            .Should().Be(ServiceCollectionExtensions.SlackClientTimeout);
    }
}
