using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <param name="Locations">The locations that could be read, by number; a number found twice keeps the last.</param>
/// <param name="Problems">What was skipped and why, in Dutch for the admin.</param>
public sealed record LocationImportResult(IReadOnlyList<Location> Locations, IReadOnlyList<string> Problems);

/// <summary>
/// Reads locations from a KML, KMZ, GeoJSON, GPX or CSV file, e.g. a layer exported from Google My Maps, uMap or a
/// spreadsheet. The number comes from a number field, or else from the first number in the name ("15 Opdracht" is 15,
/// "Opdracht"); the description becomes the hint.
/// </summary>
public static partial class LocationImport
{
    /// <summary>The largest file that is read, in bytes.</summary>
    public const long MaxFileBytes = 5 * 1024 * 1024;

    public const int MaxNumber = 9999;

    /// <summary>The most a KMZ may unpack to: a small zip must not fill the memory.</summary>
    public const long MaxUnpackedBytes = 20 * 1024 * 1024;

    private static readonly string[] NumberKeys = { "number", "nummer", "nr", "no", "locatie", "location", "id" };
    private static readonly string[] NameKeys = { "name", "naam", "title", "titel" };
    private static readonly string[] HintKeys = { "hint", "description", "omschrijving", "beschrijving", "desc", "tip" };
    private static readonly string[] LatitudeKeys = { "latitude", "lat", "breedtegraad", "y" };
    private static readonly string[] LongitudeKeys = { "longitude", "lon", "lng", "long", "lengtegraad", "x" };

    /// <summary>
    /// Reads the file as it was uploaded. A KMZ (Google My Maps' default export) is a zip with the KML inside; anything
    /// else is text.
    /// </summary>
    public static LocationImportResult ParseFile(string fileName, byte[] data)
    {
        var isZip = data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4;
        if (!isZip) return Parse(fileName, Decode(data));

        try
        {
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            // My Maps calls it doc.kml; otherwise the first KML in the archive
            var kml = zip.Entries.FirstOrDefault(e => e.FullName.Equals("doc.kml", StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase));
            if (kml == null) return Failed("Het KMZ-bestand bevat geen KML-bestand.");

            using var entry = kml.Open();
            using var unpacked = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            // Counted while reading: the size the zip claims can lie
            while ((read = entry.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (unpacked.Length + read > MaxUnpackedBytes) return Failed($"Het KMZ-bestand is uitgepakt groter dan {MaxUnpackedBytes / 1024 / 1024} MB.");
                unpacked.Write(buffer, 0, read);
            }

            return Parse("doc.kml", Decode(unpacked.ToArray()));
        }
        catch (InvalidDataException ex)
        {
            return Failed($"Het KMZ-bestand kon niet uitgepakt worden: {ex.Message}");
        }
    }

    /// <summary>UTF-8 unless a byte order mark says otherwise; Excel and My Maps both write UTF-8.</summary>
    private static string Decode(byte[] data)
    {
        using var reader = new StreamReader(new MemoryStream(data), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static LocationImportResult Failed(string problem) => new(Array.Empty<Location>(), new[] { problem });

    /// <summary>Reads the file; the extension decides the format, or the content when the extension is unknown.</summary>
    public static LocationImportResult Parse(string fileName, string content)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var trimmed = content.TrimStart('﻿', ' ', '\t', '\r', '\n');
        try
        {
            return extension switch
            {
                ".geojson" or ".json" => ParseGeoJson(trimmed),
                ".gpx" => ParseGpx(trimmed),
                ".kml" => ParseKml(trimmed),
                ".xml" or ".kmz" when IsKml(trimmed) => ParseKml(trimmed),
                ".xml" => ParseGpx(trimmed),
                ".csv" or ".txt" => ParseCsv(trimmed),
                _ when trimmed.StartsWith('{') => ParseGeoJson(trimmed),
                _ when trimmed.StartsWith('<') && IsKml(trimmed) => ParseKml(trimmed),
                _ when trimmed.StartsWith('<') => ParseGpx(trimmed),
                _ => ParseCsv(trimmed)
            };
        }
        catch (Exception ex) when (ex is JsonException or XmlException or FormatException)
        {
            return Failed($"Het bestand kon niet gelezen worden: {ex.Message}");
        }
    }

    private static bool IsKml(string content) => content.Contains("<kml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads XML without a DTD: KML and GPX never need one, and a DTD could expand into something huge.
    /// </summary>
    private static XDocument LoadXml(string content)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(content), settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    /// <summary>
    /// A KML file, as Google My Maps exports a layer (or a whole map: every layer is read). Each placemark with a point
    /// is a location: the number and name from its name ("15 Opdracht"), the hint from its description. Columns of the
    /// layer's table come along as extended data, so a column "nummer" or "hint" works too.
    /// </summary>
    public static LocationImportResult ParseKml(string content)
    {
        var document = LoadXml(content);
        var builder = new Builder();
        var placemarks = document.Descendants().Where(e => e.Name.LocalName == "Placemark").ToList();
        if (placemarks.Count == 0) builder.Problem("Het KML-bestand heeft geen punten (Placemark).");

        var index = 0;
        foreach (var placemark in placemarks)
        {
            index++;
            string? Child(string name) => placemark.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            var name = Child("name")?.Trim();
            var where = $"Punt {index}" + (string.IsNullOrWhiteSpace(name) ? "" : $" ({name})");

            var point = placemark.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point");
            var coordinates = point?.Elements().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value;
            if (coordinates == null)
            {
                builder.Problem($"{where}: geen punt (lijnen en vlakken worden overgeslagen).");
                continue;
            }

            // "lon,lat[,height]"; KML puts the longitude first, like GeoJSON
            var parts = coordinates.Trim().Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Split(',') ?? Array.Empty<string>();
            if (parts.Length < 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
                || !double.IsFinite(latitude) || !double.IsFinite(longitude))
            {
                builder.Problem($"{where}: geen geldige coördinaten.");
                continue;
            }

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var data in placemark.Descendants().Where(e => e.Name.LocalName is "Data" or "SimpleData"))
            {
                var key = (string?)data.Attribute("name");
                var value = data.Name.LocalName == "Data"
                    ? data.Elements().FirstOrDefault(e => e.Name.LocalName == "value")?.Value
                    : data.Value;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) properties.TryAdd(key.Trim(), value);
            }

            var description = PlainText(Child("description"));
            // My Maps writes the table columns into the description as well ("hint: ..."): then the column is the hint
            var hint = Pick(properties, HintKeys) is { } column ? PlainText(column) : description;
            builder.Add(where, Pick(properties, NumberKeys), name, hint, latitude, longitude);
        }

        return builder.Result();
    }

    /// <summary>
    /// A My Maps description is HTML: line breaks become new lines, tags (such as a photo) are dropped and entities
    /// decoded. The hint is shown as text, so nothing of it can run.
    /// </summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var text = LineBreak().Replace(html, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        var lines = text.Split('\n').Select(l => l.Trim());
        return string.Join("\n", lines).Trim('\n', ' ') is { Length: > 0 } result ? MultipleBlankLines().Replace(result, "\n\n") : null;
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>|</\s*(p|div|li)\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex Tag();

    [GeneratedRegex(@"\n{3,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultipleBlankLines();

    public static LocationImportResult ParseGeoJson(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var builder = new Builder();
        var root = document.RootElement;
        IEnumerable<JsonElement> features = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("features", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray()
            : root.ValueKind == JsonValueKind.Array ? root.EnumerateArray() : new[] { root };

        var index = 0;
        foreach (var feature in features)
        {
            index++;
            var where = $"Object {index}";
            if (feature.ValueKind != JsonValueKind.Object
                || !feature.TryGetProperty("geometry", out var geometry)
                || geometry.ValueKind != JsonValueKind.Object
                || !geometry.TryGetProperty("type", out var type)
                || type.GetString() != "Point"
                || !geometry.TryGetProperty("coordinates", out var coordinates)
                || coordinates.ValueKind != JsonValueKind.Array
                || coordinates.GetArrayLength() < 2
                || coordinates[0].ValueKind != JsonValueKind.Number
                || coordinates[1].ValueKind != JsonValueKind.Number)
            {
                builder.Problem($"{where}: geen punt (lijnen en vlakken worden overgeslagen).");
                continue;
            }

            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (feature.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in props.EnumerateObject())
                {
                    var value = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString(),
                        JsonValueKind.Number => property.Value.GetRawText(),
                        _ => null
                    };
                    if (value != null) properties.TryAdd(property.Name, value);
                }
            }
            if (feature.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
            {
                properties.TryAdd("id", id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString()!);
            }

            // GeoJSON puts the longitude first
            builder.Add(where, Pick(properties, NumberKeys), Pick(properties, NameKeys), Pick(properties, HintKeys),
                coordinates[1].GetDouble(), coordinates[0].GetDouble());
        }

        return builder.Result();
    }

    public static LocationImportResult ParseGpx(string content)
    {
        var document = LoadXml(content);
        var builder = new Builder();
        var points = document.Descendants().Where(e => e.Name.LocalName == "wpt").ToList();
        if (points.Count == 0) builder.Problem("Het GPX-bestand heeft geen waypoints (wpt); routes en tracks worden niet gelezen.");

        var index = 0;
        foreach (var point in points)
        {
            index++;
            string? Child(string name) => point.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            var name = Child("name");
            var where = $"Waypoint {index}" + (string.IsNullOrWhiteSpace(name) ? "" : $" ({name.Trim()})");
            if (!TryNumber((string?)point.Attribute("lat"), out var latitude) || !TryNumber((string?)point.Attribute("lon"), out var longitude))
            {
                builder.Problem($"{where}: geen geldige coördinaten.");
                continue;
            }

            builder.Add(where, null, name, Child("desc") ?? Child("cmt"), latitude, longitude);
        }

        return builder.Result();
    }

    public static LocationImportResult ParseCsv(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        var builder = new Builder();
        if (lines.Count == 0)
        {
            builder.Problem("Het bestand is leeg.");
            return builder.Result();
        }

        // Dutch spreadsheets write "52,1;5,3": a semicolon then separates and a comma is the decimal sign
        var separator = lines[0].Count(c => c == ';') >= lines[0].Count(c => c == ',') && lines[0].Contains(';') ? ';'
            : lines[0].Contains('\t') ? '\t' : ',';
        var header = SplitCsv(lines[0], separator).Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Column(string[] keys) => header.FindIndex(h => keys.Contains(h));
        var numberColumn = Column(NumberKeys);
        var nameColumn = Column(NameKeys);
        var hintColumn = Column(HintKeys);
        var latitudeColumn = Column(LatitudeKeys);
        var longitudeColumn = Column(LongitudeKeys);
        if (latitudeColumn < 0 || longitudeColumn < 0)
        {
            builder.Problem("De eerste regel moet kolomnamen hebben, met in elk geval 'lat' en 'lon' (of 'latitude' en 'longitude').");
            return builder.Result();
        }

        for (var i = 1; i < lines.Count; i++)
        {
            var cells = SplitCsv(lines[i], separator);
            string? Cell(int column) => column >= 0 && column < cells.Count ? cells[column].Trim() : null;
            var where = $"Regel {i + 1}";
            if (!TryNumber(Cell(latitudeColumn), out var latitude) || !TryNumber(Cell(longitudeColumn), out var longitude))
            {
                builder.Problem($"{where}: geen geldige coördinaten.");
                continue;
            }

            builder.Add(where, Cell(numberColumn), Cell(nameColumn), Cell(hintColumn), latitude, longitude);
        }

        return builder.Result();
    }

    private static List<string> SplitCsv(string line, char separator)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator)
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else cell.Append(c);
        }

        cells.Add(cell.ToString());
        return cells;
    }

    private static string? Pick(IReadOnlyDictionary<string, string> properties, string[] keys) =>
        keys.Select(k => properties.GetValueOrDefault(k)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value);
    }

    [GeneratedRegex(@"^\s*(?:(?:locatie|loc|location|l)\s*\.?\s*)?#?(\d{1,4})(?!\d)\s*[-:.)]?\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();

    private sealed class Builder
    {
        private readonly Dictionary<int, Location> _locations = new();
        private readonly List<string> _problems = new();

        public void Problem(string problem) => _problems.Add(problem);

        public void Add(string where, string? numberText, string? name, string? hint, double latitude, double longitude)
        {
            name = name?.Trim();
            int number;
            if (!string.IsNullOrWhiteSpace(numberText))
            {
                var digits = NumberedName().Match(numberText);
                if (!digits.Success || !int.TryParse(digits.Groups[1].Value, out number))
                {
                    Problem($"{where}: '{numberText}' is geen locatienummer.");
                    return;
                }
            }
            else if (name != null && NumberedName().Match(name) is { Success: true } match && int.TryParse(match.Groups[1].Value, out number))
            {
                // "Locatie 7 Molen" becomes "Molen"; a name that is only the number stays empty and gets a default below
                name = match.Groups[2].Value.Trim();
            }
            else
            {
                Problem($"{where}: geen locatienummer gevonden; zet het nummer in een kolom 'nummer' of vooraan de naam.");
                return;
            }

            if (number < 1 || number > MaxNumber)
            {
                Problem($"{where}: locatienummer {number} moet tussen 1 en {MaxNumber} liggen.");
                return;
            }

            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                Problem($"{where}: coördinaten {latitude}, {longitude} liggen buiten de kaart.");
                return;
            }

            if (_locations.ContainsKey(number)) Problem($"{where}: locatie {number} staat er twee keer in; de laatste telt.");

            if (string.IsNullOrWhiteSpace(name)) name = $"Locatie {number}";
            hint = string.IsNullOrWhiteSpace(hint) ? null : hint.Trim();
            if (hint?.Length > ScoutingDbContext.MaxLocationHintLength)
            {
                Problem($"{where}: de beschrijving is langer dan {ScoutingDbContext.MaxLocationHintLength} tekens en is ingekort.");
            }
            _locations[number] = new Location
            {
                Number = number,
                Name = Cut(name, ScoutingDbContext.MaxLocationNameLength),
                Hint = hint == null ? null : Cut(hint, ScoutingDbContext.MaxLocationHintLength),
                Latitude = Math.Round(latitude, 7),
                Longitude = Math.Round(longitude, 7)
            };
        }

        public LocationImportResult Result() => new(_locations.Values.OrderBy(l => l.Number).ToList(), _problems);

        private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();
    }
}
