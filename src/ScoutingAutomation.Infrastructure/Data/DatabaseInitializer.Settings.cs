using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Data;

public partial class DatabaseInitializer
{
    /// <summary>
    /// The settings made in the app live in the database, so every instance of the app sees the same ones. An older
    /// installation kept them in runtime-settings.json: that file is read in once and then renamed, so it is never used again.
    /// </summary>
    private async Task EnsureRuntimeSettingsAsync(CancellationToken ct)
    {
        var table = Sqlite
            ? """
              CREATE TABLE IF NOT EXISTS "RuntimeSettings" (
                  "Id" INTEGER NOT NULL CONSTRAINT "PK_RuntimeSettings" PRIMARY KEY,
                  "Json" TEXT NOT NULL,
                  "Version" INTEGER NOT NULL,
                  "UpdatedAt" TEXT NOT NULL)
              """
            : """
              CREATE TABLE IF NOT EXISTS "RuntimeSettings" (
                  "Id" integer NOT NULL CONSTRAINT "PK_RuntimeSettings" PRIMARY KEY,
                  "Json" text NOT NULL,
                  "Version" bigint NOT NULL,
                  "UpdatedAt" timestamp with time zone NOT NULL)
              """;
        await _context.Database.ExecuteSqlRawAsync(table, ct);

        var file = Path.GetFullPath(string.IsNullOrWhiteSpace(_options.Value.RuntimeSettingsFile)
            ? "./runtime-settings.json"
            : _options.Value.RuntimeSettingsFile);
        if (!File.Exists(file) || await _context.RuntimeSettings.AnyAsync(ct)) return;

        var json = await File.ReadAllTextAsync(file, ct);
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Left where it is, so it can be fixed by hand and read in at the next start
            _logger.LogError(ex, "{File} is damaged and was not moved to the database; the settings from appsettings apply", file);
            return;
        }

        _context.RuntimeSettings.Add(new RuntimeSettingsRecord { Json = json, Version = 1, UpdatedAt = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync(ct);

        var moved = file + ".imported";
        try
        {
            File.Move(file, moved, overwrite: true);
            _logger.LogWarning("Moved the settings from {File} to the database; the file is kept as {Moved} and no longer used", file, moved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The database has them now; the file is not read again because the database is no longer empty
            _logger.LogWarning(ex, "Moved the settings from {File} to the database; the file could not be renamed and is no longer used", file);
        }
    }
}
