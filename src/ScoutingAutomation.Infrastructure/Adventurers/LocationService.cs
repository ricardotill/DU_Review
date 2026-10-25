using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Domain.Interfaces;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Workflow;

namespace ScoutingAutomation.Infrastructure.Adventurers;

/// <summary>Manages the Avonturiers locations and the unlocks per group, and tells the live pages.</summary>
public sealed class LocationService
{
    private readonly ILocationRepository _locations;
    private readonly ISubmissionChangeNotifier _notifier;
    private readonly TimeProvider _time;

    public LocationService(ILocationRepository locations, ISubmissionChangeNotifier notifier, TimeProvider time)
    {
        _locations = locations;
        _notifier = notifier;
        _time = time;
    }

    public Task<IReadOnlyList<Location>> GetAllAsync(CancellationToken ct = default) => _locations.GetAllAsync(ct);

    public Task<IReadOnlyList<LocationUnlock>> GetUnlocksAsync(Guid? groupId = null, CancellationToken ct = default) =>
        _locations.GetUnlocksAsync(groupId, ct);

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

        await _locations.UpsertAsync(new Location
        {
            Number = location.Number,
            Name = location.Name.Trim(),
            Hint = string.IsNullOrWhiteSpace(location.Hint) ? null : location.Hint.Trim(),
            Latitude = location.Latitude,
            Longitude = location.Longitude
        }, ct);
        _notifier.NotifyChanged();
        return null;
    }

    public async Task DeleteAsync(int number, CancellationToken ct = default)
    {
        await _locations.DeleteAsync(number, ct);
        _notifier.NotifyChanged();
    }

    /// <summary>Reads a GeoJSON, GPX or CSV file and adds or replaces its locations; others are kept.</summary>
    public async Task<LocationImportResult> ImportAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var reader = new StreamReader(content);
        var buffer = new char[LocationImport.MaxFileBytes + 1];
        var read = await reader.ReadBlockAsync(buffer, ct);
        if (read > LocationImport.MaxFileBytes)
        {
            return new LocationImportResult(Array.Empty<Location>(), new[] { $"Het bestand is te groot: maximaal {LocationImport.MaxFileBytes / 1024 / 1024} MB." });
        }

        var result = LocationImport.Parse(fileName, new string(buffer, 0, read));
        if (result.Locations.Count > 0)
        {
            await _locations.UpsertManyAsync(result.Locations, ct);
            _notifier.NotifyChanged();
        }

        return result;
    }

    /// <summary>Unlocks or locks one location for one group: it decides what the group's map shows.</summary>
    public async Task<bool> SetUnlockedAsync(Guid groupId, int number, bool unlocked, CancellationToken ct = default)
    {
        var changed = await _locations.SetUnlockedAsync(groupId, number, unlocked, _time.GetUtcNow(), ct);
        if (changed) _notifier.NotifyChanged();
        return changed;
    }
}
