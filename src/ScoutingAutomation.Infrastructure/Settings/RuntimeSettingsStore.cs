using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Infrastructure.Settings;

/// <summary>The settings made in the app, as one row: shared by every instance of the app on the database.</summary>
public class RuntimeSettingsRecord
{
    public const int SingleId = 1;

    public int Id { get; set; } = SingleId;

    public string Json { get; set; } = "{}";

    /// <summary>Goes up by one with each save, so a save based on settings another instance already changed is refused.</summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Where <see cref="RuntimeSettingsService"/> keeps its settings.</summary>
public interface IRuntimeSettingsStore
{
    /// <summary>The saved settings and their version; (null, 0) when nothing was saved yet.</summary>
    (string? Json, long Version) Load();

    /// <summary>Saves when the stored version is still <paramref name="expectedVersion"/>.</summary>
    /// <returns>The new version, or null when someone else saved in between.</returns>
    long? TrySave(string json, long expectedVersion);
}

/// <summary>Keeps the settings in memory: for tests, and for code that runs without a database.</summary>
public sealed class InMemoryRuntimeSettingsStore : IRuntimeSettingsStore
{
    private readonly object _lock = new();
    private string? _json;
    private long _version;

    public InMemoryRuntimeSettingsStore(string? json = null)
    {
        _json = json;
        _version = json == null ? 0 : 1;
    }

    public (string? Json, long Version) Load()
    {
        lock (_lock) return (_json, _version);
    }

    public long? TrySave(string json, long expectedVersion)
    {
        lock (_lock)
        {
            if (_version != expectedVersion) return null;
            _json = json;
            return ++_version;
        }
    }
}

/// <summary>Keeps the settings in the <c>RuntimeSettings</c> table.</summary>
public sealed class DatabaseRuntimeSettingsStore(IServiceScopeFactory scopes, TimeProvider time) : IRuntimeSettingsStore
{
    // The settings are first read before DatabaseInitializer has made the table (a new database);
    // checking for it first keeps that start free of query errors in the log.
    private volatile bool _tableSeen;

    public (string? Json, long Version) Load()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScoutingDbContext>();
        if (!_tableSeen && db.Database.IsRelational())
        {
            if (!TableExists(db)) return (null, 0);
            _tableSeen = true;
        }

        var row = db.RuntimeSettings.AsNoTracking().FirstOrDefault(r => r.Id == RuntimeSettingsRecord.SingleId);
        return row == null ? (null, 0) : (row.Json, row.Version);
    }

    private static bool TableExists(ScoutingDbContext db)
    {
        var sql = db.Database.IsNpgsql()
            ? """SELECT COUNT(*)::int AS "Value" FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'RuntimeSettings'"""
            : """SELECT COUNT(*) AS "Value" FROM sqlite_master WHERE type = 'table' AND name = 'RuntimeSettings'""";
        return db.Database.SqlQueryRaw<int>(sql).AsEnumerable().First() > 0;
    }

    public long? TrySave(string json, long expectedVersion)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ScoutingDbContext>();
        var now = time.GetUtcNow();
        if (expectedVersion == 0)
        {
            db.RuntimeSettings.Add(new RuntimeSettingsRecord { Json = json, Version = 1, UpdatedAt = now });
            try
            {
                db.SaveChanges();
                return 1;
            }
            catch (DbUpdateException ex) when (ex.InnerException is DbException)
            {
                // Another instance saved the first settings at the same moment
                return null;
            }
        }

        if (!db.Database.IsRelational())
        {
            // The in-memory provider of the tests has no ExecuteUpdate; nothing runs side by side there
            var row = db.RuntimeSettings.FirstOrDefault(r => r.Id == RuntimeSettingsRecord.SingleId);
            if (row == null || row.Version != expectedVersion) return null;
            row.Json = json;
            row.Version = expectedVersion + 1;
            row.UpdatedAt = now;
            db.SaveChanges();
            return row.Version;
        }

        var updated = db.RuntimeSettings
            .Where(r => r.Id == RuntimeSettingsRecord.SingleId && r.Version == expectedVersion)
            .ExecuteUpdate(s => s
                .SetProperty(r => r.Json, json)
                .SetProperty(r => r.Version, expectedVersion + 1)
                .SetProperty(r => r.UpdatedAt, now));
        return updated == 1 ? expectedVersion + 1 : null;
    }
}

/// <summary>Saving the settings failed; the settings in memory are back to what is stored.</summary>
public sealed class RuntimeSettingsSaveException(string message, Exception? inner = null, bool changedElsewhere = false) : IOException(message, inner)
{
    /// <summary>Another instance saved first; trying again on the settings just read back can work.</summary>
    public bool ChangedElsewhere { get; } = changedElsewhere;
}
