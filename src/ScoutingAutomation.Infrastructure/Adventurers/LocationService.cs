using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Settings;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>
/// Manages the Avonturiers locations and how many are open per group, and tells the live pages. Every change can open
/// or close assignments, so the groups' Toeristen are told afterwards.
/// </summary>
public sealed class LocationService
{
    private readonly ILocationRepository _locations;
    private readonly ISubmissionChangeNotifier _notifier;
    private readonly IGroupRepository? _groups;
    private readonly UnlockNotifier? _unlocks;
    private readonly RuntimeSettingsService? _runtime;

    public LocationService(ILocationRepository locations, ISubmissionChangeNotifier notifier, IGroupRepository? groups = null,
        UnlockNotifier? unlocks = null, RuntimeSettingsService? runtime = null)
    {
        _locations = locations;
        _notifier = notifier;
        _groups = groups;
        _unlocks = unlocks;
        _runtime = runtime;
    }

    public Task<IReadOnlyList<Location>> GetAllAsync(CancellationToken ct = default) => _locations.GetAllAsync(ct);

    /// <summary>Why the location cannot be saved, or null when it can.</summary>
    public static string? Validate(Location location)
    {
        if (location.Number is < 1 or > LocationImport.MaxNumber) return $"Het nummer moet tussen 1 en {LocationImport.MaxNumber} liggen.";
        if (string.IsNullOrWhiteSpace(location.Name)) return "Geef de locatie een naam.";
        if (location.Name.Trim().Length > ScoutingDbContext.MaxLocationNameLength) return $"De naam is te lang: maximaal {ScoutingDbContext.MaxLocationNameLength} tekens.";
        if (location.Hint?.Trim().Length > ScoutingDbContext.MaxLocationHintLength) return $"De hint is te lang: maximaal {ScoutingDbContext.MaxLocationHintLength} tekens.";
        if (!double.IsFinite(location.Latitude) || location.Latitude is < -90 or > 90) return "De breedtegraad moet tussen -90 en 90 liggen.";
        if (!double.IsFinite(location.Longitude) || location.Longitude is < -180 or > 180) return "De lengtegraad moet tussen -180 en 180 liggen.";
        return null;
    }

    /// <summary>Adds the location or replaces the one with its number.</summary>
    /// <returns>Why it was not saved, or null when it was.</returns>
    public async Task<string?> SaveAsync(Location location, CancellationToken ct = default)
    {
        if (Validate(location) is { } problem) return problem;

        var first = await HasNoLocationsAsync(ct);
        await _locations.UpsertAsync(new Location
        {
            Number = location.Number,
            Name = location.Name.Trim(),
            Hint = string.IsNullOrWhiteSpace(location.Hint) ? null : location.Hint.Trim(),
            Latitude = location.Latitude,
            Longitude = location.Longitude
        }, ct);
        if (first) _runtime?.ResetUnlockNotified();
        await ChangedForAllAsync(ct);
        return null;
    }

    public async Task DeleteAsync(int number, CancellationToken ct = default)
    {
        await _locations.DeleteAsync(number, ct);
        await ChangedForAllAsync(ct);
    }

    /// <summary>Reads a KML, KMZ, GeoJSON, GPX or CSV file and adds or replaces its locations; others are kept.</summary>
    public async Task<LocationImportResult> ImportAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        // Bytes, not text: a KMZ is a zip. One byte more than allowed tells that the file is too large.
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > LocationImport.MaxFileBytes)
            {
                return new LocationImportResult(Array.Empty<Location>(), new[] { $"Het bestand is te groot: maximaal {LocationImport.MaxFileBytes / 1024 / 1024} MB." });
            }

            buffer.Write(chunk, 0, read);
        }

        var result = LocationImport.ParseFile(fileName, buffer.ToArray());
        if (result.Locations.Count > 0)
        {
            var first = await HasNoLocationsAsync(ct);
            await _locations.UpsertManyAsync(result.Locations, ct);
            if (first) _runtime?.ResetUnlockNotified();
            await ChangedForAllAsync(ct);
        }

        return result;
    }

    /// <summary>
    /// Opens locations 1 through <paramref name="openLocations"/> for the group; null goes back to the number every
    /// group starts with. It decides what the group's map shows and which check-ins count.
    /// </summary>
    /// <returns>Why it was not saved, or null when it was.</returns>
    public async Task<string?> SetOpenLocationsAsync(Guid groupId, int? openLocations, CancellationToken ct = default)
    {
        if (openLocations is < 0 or > LocationImport.MaxNumber) return $"Kies een nummer tussen 0 en {LocationImport.MaxNumber}.";

        if (await _locations.SetOpenLocationsAsync(groupId, openLocations, ct))
        {
            _notifier.NotifyChanged();
            _unlocks?.Enqueue(groupId);
        }

        return null;
    }

    /// <summary>
    /// Without locations every assignment is open. Once the first ones arrive the counts start again from the
    /// assignments open at the start, so what the groups were told before is forgotten (see <see cref="UnlockNotifier"/>).
    /// </summary>
    private async Task<bool> HasNoLocationsAsync(CancellationToken ct) => (await _locations.GetAllAsync(ct)).Count == 0;

    /// <summary>
    /// The number of locations open at the start, the number of locations, or a location's number changed: any group
    /// may now have more or fewer open assignments.
    /// </summary>
    public async Task ChangedForAllAsync(CancellationToken ct = default)
    {
        _notifier.NotifyChanged();
        if (_groups == null || _unlocks == null) return;

        foreach (var group in await _groups.GetAllAsync(ct))
        {
            _unlocks.Enqueue(group.Id);
        }
    }
}
