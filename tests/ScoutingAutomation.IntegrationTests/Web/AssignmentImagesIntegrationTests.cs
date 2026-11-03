using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Assignments;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Web;

/// <summary>
/// The pictures of the assignments and the group's own list of open assignments: the jury sees everything, a group only
/// what its Avonturiers opened. 150 of the 160 assignments are open at the start, with 10 locations.
/// </summary>
public class AssignmentImagesIntegrationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private static readonly byte[] Png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

    private readonly TestApp _app;
    private Group _group = default!;
    private AssignmentImage _open = default!;
    private AssignmentImage _locked = default!;

    public AssignmentImagesIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _app = new TestApp(factory, new Dictionary<string, string?>
        {
            ["Scouting:Adventurers:Enabled"] = "true",
            ["Scouting:Adventurers:InitialOpenAssignments"] = "150"
        });
    }

    public async Task InitializeAsync()
    {
        _group = (await _app.GetAsync(sp => sp.GetRequiredService<IGroupRepository>().GetBySlackChannelIdAsync("C01ZEEARENDEN")))!;
        await _app.GetAsync(async sp =>
        {
            var csv = "nummer;naam;lat;lon\n" + string.Join("\n", Enumerable.Range(1, 10).Select(n => $"{n};Plek {n};51.{n};5.{n}"));
            await sp.GetRequiredService<LocationService>().ImportAsync("locaties.csv", new MemoryStream(Encoding.UTF8.GetBytes(csv)));
            var edit = sp.GetRequiredService<AssignmentEditService>();
            (await edit.AddImageAsync("150", "open.png", new MemoryStream(Png))).Should().BeNull();
            (await edit.AddImageAsync("151", "dicht.png", new MemoryStream(Png))).Should().BeNull();
            (await edit.SaveAsync("151", new AssignmentEdit("Geheime opdracht", "Niet voor de groep", 1, 2), "test")).Should().BeNull();
            return true;
        });
        _open = (await _app.GetAsync(sp => sp.GetRequiredService<IAssignmentImageRepository>().GetForAssignmentAsync("150"))).Single();
        _locked = (await _app.GetAsync(sp => sp.GetRequiredService<IAssignmentImageRepository>().GetForAssignmentAsync("151"))).Single();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> CreateGroupClientAsync()
    {
        await _app.EnsureAccountAsync("groep@du.test", AccountKind.Group, _group.Id);
        return await _app.CreateClientForAsync("groep@du.test");
    }

    [Fact]
    public async Task Jury_ShouldGetEveryPicture()
    {
        using var client = await _app.CreateLoggedInClientAsync();

        var open = await client.GetAsync($"/api/assignment-images/{_open.Id}");
        var locked = await client.GetAsync($"/api/assignment-images/{_locked.Id}");

        open.StatusCode.Should().Be(HttpStatusCode.OK);
        open.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        open.Headers.GetValues("Content-Security-Policy").Single().Should().StartWith("sandbox");
        (await open.Content.ReadAsByteArrayAsync()).Should().Equal(Png);
        locked.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Group_ShouldOnlyGetPicturesOfOpenAssignments()
    {
        using var client = await CreateGroupClientAsync();

        (await client.GetAsync($"/api/assignment-images/{_open.Id}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"/api/assignment-images/{_locked.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/assignment-images/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WithoutLogin_ShouldGetNoPicture()
    {
        using var client = _app.CreateClient();

        var response = await client.GetAsync($"/api/assignment-images/{_open.Id}");

        response.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GroupPage_ShouldListOpenAssignmentsButNotTheLockedText()
    {
        using var client = await CreateGroupClientAsync();

        var response = await client.GetAsync("/mijn-opdrachten");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("Onze opdrachten").And.NotContain("Geheime opdracht").And.NotContain("Niet voor de groep");
    }

    [Fact]
    public async Task GroupMapPage_ShouldOpenForTheGroup()
    {
        using var client = await CreateGroupClientAsync();

        var response = await client.GetAsync("/onze-kaart");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Onze kaart");
    }
}
