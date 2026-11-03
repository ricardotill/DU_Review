using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>The map a group opens from the link in Slack: no login, the token is the key, only its open locations.</summary>
public class GroupMapIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly TestApp _app;
    private Group _group = default!;

    public GroupMapIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory, new Dictionary<string, string?> { ["Scouting:Adventurers:Enabled"] = "true" });
    }

    public async Task InitializeAsync()
    {
        _group = (await _app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync("C01ZEEARENDEN")))!;
        await _app.GetAsync(async sp =>
        {
            var locations = sp.GetRequiredService<LocationService>();
            await locations.SaveAsync(new Location { Number = 7, Name = "Molen", Latitude = 51.6, Longitude = 5.0, Hint = "Bij de brug" });
            await locations.SaveAsync(new Location { Number = 8, Name = "Geheime plek", Latitude = 51.7, Longitude = 5.1 });
            // Locations 1 through 7 are open for the group: of the two, only 7
            return await locations.SetOpenLocationsAsync(_group.Id, 7);
        });
    }

    [Fact]
    public async Task MapData_ShouldShowOnlyTheOpenLocationsWithoutLogin()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync($"/api/kaart/{_group.MapToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("group").GetString().Should().Be("De Zeearenden");
        var markers = json.RootElement.GetProperty("markers").EnumerateArray().ToList();
        markers.Should().ContainSingle();
        markers[0].GetProperty("number").GetInt32().Should().Be(7);
        markers[0].GetProperty("photos").GetArrayLength().Should().Be(0, "photos stay behind the login");
    }

    [Fact]
    public async Task MapData_ShouldMarkOnlyTheSuperLocations()
    {
        using var client = _app.CreateClient();
        async Task<bool> SuperOf7()
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/kaart/{_group.MapToken}"));
            return json.RootElement.GetProperty("markers").EnumerateArray().Single().GetProperty("super").GetBoolean();
        }

        await _app.GetAsync(sp => Task.FromResult(sp.GetRequiredService<RuntimeSettingsService>().SetSuperLocations(true, new[] { 8 })));
        (await SuperOf7()).Should().BeFalse();

        await _app.GetAsync(sp => Task.FromResult(sp.GetRequiredService<RuntimeSettingsService>().SetSuperLocations(true, new[] { 7 })));
        (await SuperOf7()).Should().BeTrue();
    }

    [Fact]
    public async Task Page_ShouldOpenWithoutLogin()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync($"/kaart/{_group.MapToken}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("De Zeearenden").And.Contain("duMap.publicMap");
    }

    [Fact]
    public async Task BeforeTheAvonturiersStart_ShouldShowNoLocations()
    {
        await _app.GetAsync(sp => Task.FromResult(sp.GetRequiredService<RuntimeSettingsService>()
            .SetAdventurersPostTime(false, DateTimeOffset.UtcNow.AddHours(1).ToGameTime().DateTime)));
        using var client = _app.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync($"/api/kaart/{_group.MapToken}"));
        json.RootElement.GetProperty("markers").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("startsAt").GetString().Should().NotBeNullOrEmpty();

        var html = await client.GetStringAsync($"/kaart/{_group.MapToken}");
        html.Should().Contain("zodra de Avonturiers beginnen").And.NotContain("duMap.publicMap").And.NotContain("Molen");
    }

    [Fact]
    public async Task WrongToken_ShouldShowNothing()
    {
        using var client = _app.CreateClient();

        (await client.GetAsync("/api/kaart/geraden")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetStringAsync("/kaart/geraden")).Should().Contain("bestaat niet").And.NotContain("De Zeearenden");
    }

    [Fact]
    public async Task JuryPages_ShouldStillNeedALogin()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync("/avonturiers/kaart");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();
}
