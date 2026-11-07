using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NSubstitute;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
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

    private readonly UnlockNotifier _unlocks;

    public LocationServiceTests()
    {
        var settings = new ScoutingSettings { Adventurers = new AdventurersSettings { Enabled = true } };
        // Never started: what it is asked to do stays in its queue, where Pending counts it
        _unlocks = new UnlockNotifier(Substitute.For<IServiceScopeFactory>(), TestRuntime.For(Options.Create(settings)), NullLogger<UnlockNotifier>.Instance);
    }

    private LocationService Service() => new(new LocationRepository(_db), _notifier, new GroupRepository(_db), _unlocks);

    private async Task<Guid> AddGroupAsync(string name = "De Wolven")
    {
        var group = new Group { Name = name, SlackChannelId = $"C-{name}" };
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
    public async Task Delete_ShouldRemoveTheLocationAndTellEveryGroup()
    {
        var service = Service();
        await AddGroupAsync("A");
        await AddGroupAsync("B");
        await service.SaveAsync(new Location { Number = 7, Name = "Molen", Latitude = 51.6, Longitude = 5.0 });
        var before = _unlocks.Pending;

        await service.DeleteAsync(7);

        (await service.GetAllAsync()).Should().BeEmpty();
        _unlocks.Pending.Should().Be(before + 2, "with one location fewer, every group may have other assignments open");
    }

    [Fact]
    public async Task SetOpenLocations_ShouldSaveTellTheGroupAndOnlyCountARealChange()
    {
        var service = Service();
        var group = await AddGroupAsync();

        (await service.SetOpenLocationsAsync(group, 15)).Should().BeNull();
        (await service.SetOpenLocationsAsync(group, 15)).Should().BeNull();

        (await _db.Groups.AsNoTracking().SingleAsync(g => g.Id == group)).OpenLocations.Should().Be(15);
        _unlocks.Pending.Should().Be(1);
        _notifier.Received(1).NotifyChanged();

        (await service.SetOpenLocationsAsync(group, null)).Should().BeNull();
        (await _db.Groups.AsNoTracking().SingleAsync(g => g.Id == group)).OpenLocations.Should().BeNull("empty goes back to the number every group starts with");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10000)]
    public async Task SetOpenLocations_OutOfRange_ShouldSayWhy(int number)
    {
        var service = Service();
        var group = await AddGroupAsync();

        (await service.SetOpenLocationsAsync(group, number)).Should().NotBeNull();
        (await _db.Groups.AsNoTracking().SingleAsync(g => g.Id == group)).OpenLocations.Should().BeNull();
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

    /// <summary>A layer as Google My Maps exports it: a folder, styles, HTML in CDATA, a photo, a route.</summary>
    private const string MyMapsKml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2">
          <Document>
            <name>DU 2026</name>
            <Style id="icon-1899-0288D1-normal"><IconStyle><scale>1</scale></IconStyle></Style>
            <Folder>
              <name>Locaties</name>
              <Placemark>
                <name>15 Opdracht</name>
                <description><![CDATA[Zoek de oude molen.<br>Achter de brug &amp; dan links.<br><br><br><img src="https://mymaps.usercontent.google.com/foto.jpg" height="200" width="auto" />]]></description>
                <styleUrl>#icon-1899-0288D1</styleUrl>
                <ExtendedData>
                  <Data name="gx_media_links"><value><![CDATA[https://mymaps.usercontent.google.com/foto.jpg]]></value></Data>
                </ExtendedData>
                <Point>
                  <coordinates>
                    5.0123,51.6,0
                  </coordinates>
                </Point>
              </Placemark>
              <Placemark>
                <name>16 Kerk &amp; toren</name>
                <Point><coordinates>5.1,51.7,0</coordinates></Point>
              </Placemark>
              <Placemark>
                <name>Route</name>
                <LineString><coordinates>5,51,0 5.1,51.1,0</coordinates></LineString>
              </Placemark>
              <Placemark>
                <name>Parkeerplaats</name>
                <Point><coordinates>5,51,0</coordinates></Point>
              </Placemark>
            </Folder>
          </Document>
        </kml>
        """;

    [Fact]
    public async Task Import_MyMapsKml_ShouldReadNumberNameAndDescription()
    {
        var result = await Service().ImportAsync("DU 2026.kml", Text(MyMapsKml));

        result.Locations.Select(l => (l.Number, l.Name, l.Latitude, l.Longitude, l.Hint)).Should().Equal(
            (15, "Opdracht", 51.6, 5.0123, "Zoek de oude molen.\nAchter de brug & dan links."),
            (16, "Kerk & toren", 51.7, 5.1, (string?)null));
        result.Problems.Should().HaveCount(2);
        result.Problems.Should().Contain(p => p.Contains("Route")).And.Contain(p => p.Contains("Parkeerplaats"));
        (await Service().GetAllAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Import_MyMapsKmz_ShouldReadTheKmlInside()
    {
        using var kmz = new MemoryStream();
        using (var zip = new ZipArchive(kmz, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("images/foto.jpg").Open().Dispose();
            await using var entry = zip.CreateEntry("doc.kml").Open();
            await entry.WriteAsync(Encoding.UTF8.GetBytes(MyMapsKml));
        }

        kmz.Position = 0;
        var result = await Service().ImportAsync("DU 2026.kmz", kmz);

        result.Locations.Select(l => l.Number).Should().Equal(15, 16);
    }

    [Fact]
    public void Import_KmzThatUnpacksTooLarge_ShouldStop()
    {
        using var kmz = new MemoryStream();
        using (var zip = new ZipArchive(kmz, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("doc.kml", CompressionLevel.SmallestSize).Open())
        {
            entry.Write(new byte[LocationImport.MaxUnpackedBytes + 1]);
        }

        kmz.Length.Should().BeLessThan(LocationImport.MaxFileBytes, "a few zeros pack small");
        var result = LocationImport.ParseFile("bom.kmz", kmz.ToArray());

        result.Locations.Should().BeEmpty();
        result.Problems.Single().Should().Contain("uitgepakt");
    }

    [Fact]
    public void Import_KmlWithADtd_ShouldBeRefused()
    {
        const string kml = """
            <?xml version="1.0"?>
            <!DOCTYPE kml [<!ENTITY a "aaaaaaaaaa">]>
            <kml xmlns="http://www.opengis.net/kml/2.2"><Placemark><name>1 &a;</name><Point><coordinates>5,51</coordinates></Point></Placemark></kml>
            """;

        var result = LocationImport.Parse("kaart.kml", kml);

        result.Locations.Should().BeEmpty();
        result.Problems.Single().Should().Contain("kon niet gelezen");
    }

    [Fact]
    public void Import_KmlWithAColumnNummer_ShouldUseIt()
    {
        const string kml = """
            <kml xmlns="http://www.opengis.net/kml/2.2"><Document><Placemark>
              <name>Molen</name>
              <ExtendedData><Data name="nummer"><value>7</value></Data><Data name="hint"><value>Bij de brug</value></Data></ExtendedData>
              <Point><coordinates>5,51,0</coordinates></Point>
            </Placemark></Document></kml>
            """;

        var result = LocationImport.Parse("kaart.kml", kml);

        result.Locations.Single().Should().BeEquivalentTo(new { Number = 7, Name = "Molen", Hint = "Bij de brug" });
    }

    [Theory]
    [InlineData("Eerst<br/>dan<BR>klaar", "Eerst\ndan\nklaar")]
    [InlineData("<p>Een</p><p>Twee &lt;3</p>", "Een\nTwee <3")]
    [InlineData("<img src=\"x.jpg\" />", null)]
    [InlineData("  ", null)]
    public void PlainText_ShouldTurnMyMapsHtmlIntoLines(string html, string? expected)
    {
        LocationImport.PlainText(html).Should().Be(expected);
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
