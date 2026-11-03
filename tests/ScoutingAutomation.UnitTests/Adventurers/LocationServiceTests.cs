using System.Text;
using FluentAssertions;
using NSubstitute;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Adventurers;
using ScoutingAutomation.Infrastructure.Repositories;
using ScoutingAutomation.Infrastructure.Workflow;
using Xunit;

namespace ScoutingAutomation.UnitTests.Adventurers;

public class LocationServiceTests
{
    private readonly ISubmissionChangeNotifier _notifier = Substitute.For<ISubmissionChangeNotifier>();
    private readonly Infrastructure.Data.ScoutingDbContext _db = TestDb.Create();

    private LocationService Service() => new(new LocationRepository(_db), _notifier, TimeProvider.System);

    private async Task<Guid> AddGroupAsync()
    {
        var group = new Group { Name = "Groep", SlackChannelId = "C" + Guid.NewGuid().ToString("N")[..8] };
        _db.Groups.Add(group);
        await _db.SaveChangesAsync();
        return group.Id;
    }

    private static Stream Text(string content) => new MemoryStream(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Save_ShouldAddAndThenReplaceByNumber()
    {
        var service = Service();

        (await service.SaveAsync(new Location { Number = 7, Name = " Molen ", Latitude = 51.6, Longitude = 5.0, Hint = " " })).Should().BeNull();
        (await service.SaveAsync(new Location { Number = 7, Name = "Oude molen", Latitude = 51.7, Longitude = 5.1 })).Should().BeNull();

        var all = await service.GetAllAsync();
        all.Should().ContainSingle();
        all[0].Name.Should().Be("Oude molen");
        all[0].Latitude.Should().Be(51.7);
        _notifier.Received(2).NotifyChanged();
    }

    [Theory]
    [InlineData(0, "Molen", 51, 5)]
    [InlineData(7, " ", 51, 5)]
    [InlineData(7, "Molen", 91, 5)]
    [InlineData(7, "Molen", 51, -181)]
    public async Task Save_Invalid_ShouldSayWhyAndSaveNothing(int number, string name, double lat, double lng)
    {
        var service = Service();

        (await service.SaveAsync(new Location { Number = number, Name = name, Latitude = lat, Longitude = lng })).Should().NotBeNull();
        (await service.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_ShouldRemoveTheLocationAndItsUnlocks()
    {
        var service = Service();
        var group = await AddGroupAsync();
        await service.SaveAsync(new Location { Number = 7, Name = "Molen", Latitude = 51.6, Longitude = 5.0 });
        await service.SetUnlockedAsync(group, 7, true);

        await service.DeleteAsync(7);

        (await service.GetAllAsync()).Should().BeEmpty();
        (await service.GetUnlocksAsync(group)).Should().BeEmpty();
    }

    [Fact]
    public async Task SetUnlocked_ShouldOnlyReportARealChange()
    {
        var service = Service();
        var group = await AddGroupAsync();

        (await service.SetUnlockedAsync(group, 7, true)).Should().BeTrue();
        (await service.SetUnlockedAsync(group, 7, true)).Should().BeFalse();
        (await service.GetUnlocksAsync(group)).Select(u => u.LocationNumber).Should().Equal(7);
        (await service.SetUnlockedAsync(group, 7, false)).Should().BeTrue();
        (await service.GetUnlocksAsync(group)).Should().BeEmpty();
    }

    [Fact]
    public async Task Import_GeoJson_ShouldReadPointsAndSkipTheRest()
    {
        const string geoJson = """
        {
          "type": "FeatureCollection",
          "features": [
            { "type": "Feature", "properties": { "name": "Locatie 7 - Molen", "description": "Bij de brug" }, "geometry": { "type": "Point", "coordinates": [5.0123, 51.6] } },
            { "type": "Feature", "properties": { "nummer": 12, "naam": "Kerk" }, "geometry": { "type": "Point", "coordinates": [5.1, 51.7, 3.0] } },
            { "type": "Feature", "properties": { "name": "Route" }, "geometry": { "type": "LineString", "coordinates": [[5, 51], [5.1, 51.1]] } },
            { "type": "Feature", "properties": { "name": "Zonder nummer" }, "geometry": { "type": "Point", "coordinates": [5, 51] } }
          ]
        }
        """;

        var result = await Service().ImportAsync("locaties.geojson", Text(geoJson));

        result.Locations.Select(l => (l.Number, l.Name, l.Latitude, l.Longitude, l.Hint))
            .Should().Equal((7, "Molen", 51.6, 5.0123, "Bij de brug"), (12, "Kerk", 51.7, 5.1, (string?)null));
        result.Problems.Should().HaveCount(2);
        (await Service().GetAllAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_Gpx_ShouldReadWaypoints()
    {
        const string gpx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx version="1.1" creator="test" xmlns="http://www.topografix.com/GPX/1/1">
          <wpt lat="51.6" lon="5.0"><name>7 Molen</name><desc>Bij de brug</desc></wpt>
          <wpt lat="51.7" lon="5.1"><name>L8</name></wpt>
          <wpt lat="x" lon="5.1"><name>9 Kapot</name></wpt>
        </gpx>
        """;

        var result = await Service().ImportAsync("route.gpx", Text(gpx));

        result.Locations.Select(l => (l.Number, l.Name, l.Hint)).Should().Equal((7, "Molen", "Bij de brug"), (8, "Locatie 8", (string?)null));
        result.Problems.Should().ContainSingle().Which.Should().Contain("9 Kapot");
    }

    [Fact]
    public async Task Import_DutchCsv_ShouldUseSemicolonsAndDecimalCommas()
    {
        const string csv = "nummer;naam;lat;lon;hint\r\n7;Molen;51,6;5,0123;\"Bij de brug; links\"\r\n8;Kerk;51.7;5.1;\r\n;Zonder nummer;51;5;\r\n";

        var result = await Service().ImportAsync("locaties.csv", Text(csv));

        result.Locations.Select(l => (l.Number, l.Name, l.Latitude, l.Longitude, l.Hint))
            .Should().Equal((7, "Molen", 51.6, 5.0123, "Bij de brug; links"), (8, "Kerk", 51.7, 5.1, (string?)null));
        result.Problems.Should().ContainSingle().Which.Should().Contain("Regel 4");
    }

    [Fact]
    public async Task Import_CsvWithoutCoordinateColumns_ShouldSayWhat()
    {
        var result = await Service().ImportAsync("locaties.csv", Text("nummer,naam\n7,Molen\n"));

        result.Locations.Should().BeEmpty();
        result.Problems.Single().Should().Contain("lat");
    }

    [Fact]
    public async Task Import_BrokenFile_ShouldReportAndKeepExisting()
    {
        var service = Service();
        await service.SaveAsync(new Location { Number = 1, Name = "Start", Latitude = 51, Longitude = 5 });

        var result = await service.ImportAsync("kapot.geojson", Text("{ niet af"));

        result.Locations.Should().BeEmpty();
        result.Problems.Should().ContainSingle();
        (await service.GetAllAsync()).Should().ContainSingle();
    }

    [Fact]
    public void Import_DuplicateNumber_ShouldKeepTheLast()
    {
        var result = LocationImport.ParseCsv("nummer,naam,lat,lon\n7,Eerste,51,5\n7,Tweede,52,6\n");

        result.Locations.Single().Name.Should().Be("Tweede");
        result.Problems.Should().ContainSingle().Which.Should().Contain("twee keer");
    }
}
