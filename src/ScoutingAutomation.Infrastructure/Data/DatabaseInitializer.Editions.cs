using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ScoutingAutomation.Domain.Entities;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Data;

/// <summary>
/// Editions: a database from before them holds one game. It becomes the first edition, every group gets a team (with
/// the group's id, so group accounts keep pointing at it) and assignments and locations get the edition.
/// </summary>
public partial class DatabaseInitializer
{
    private bool Sqlite => _context.Database.IsSqlite();

    private async Task MoveToEditionsAsync(CancellationToken ct)
    {
        await AddEditionTablesAsync(ct);

        var needed = !await ColumnExistsAsync("Groups", "EditionId", ct)
            || !await ColumnExistsAsync("Groups", "TeamId", ct)
            || !await ColumnExistsAsync("Assignments", "EditionId", ct)
            || !await ColumnExistsAsync("Locations", "EditionId", ct);
        if (needed)
        {
            await BackUpBeforeEditionsAsync(ct);
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _context.Database.BeginTransactionAsync(ct);
                var edition = await FirstEditionIdAsync(ct);
                await AddEditionColumnsAsync(edition, ct);
                await transaction.CommitAsync(ct);
            });
            _context.ChangeTracker.Clear();
            _logger.LogInformation("Moved the existing game to the first edition");
        }

        // Before editions these were unique over all rows; now per edition
        await _context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Groups_SlackChannelId\"", ct);
        await _context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Groups_AdventurersSlackChannelId\"", ct);
        await _context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Assignments_Code\"", ct);

        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.ActiveEditionIndexName}\" ON \"Editions\" (\"Status\") WHERE \"Status\" = 'Active'", ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.GroupChannelIndexName}\" ON \"Groups\" (\"EditionId\", \"SlackChannelId\")", ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.AdventurersChannelIndexName}\" ON \"Groups\" " +
            "(\"EditionId\", \"AdventurersSlackChannelId\") WHERE \"AdventurersSlackChannelId\" IS NOT NULL", ct);
        await _context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS \"IX_Groups_TeamId\" ON \"Groups\" (\"TeamId\")", ct);
        await _context.Database.ExecuteSqlRawAsync(
            $"CREATE UNIQUE INDEX IF NOT EXISTS \"{ScoutingDbContext.AssignmentCodeIndexName}\" ON \"Assignments\" (\"EditionId\", \"Code\")", ct);
    }

    private async Task AddEditionTablesAsync(CancellationToken ct)
    {
        var editions = Sqlite
            ? """
              CREATE TABLE IF NOT EXISTS "Editions" (
                  "Id" TEXT NOT NULL CONSTRAINT "PK_Editions" PRIMARY KEY,
                  "Name" TEXT NOT NULL,
                  "Year" INTEGER NOT NULL,
                  "Status" TEXT NOT NULL,
                  "CreatedAt" TEXT NOT NULL)
              """
            : $"""
              CREATE TABLE IF NOT EXISTS "Editions" (
                  "Id" uuid NOT NULL CONSTRAINT "PK_Editions" PRIMARY KEY,
                  "Name" character varying({Edition.MaxNameLength}) NOT NULL,
                  "Year" integer NOT NULL,
                  "Status" character varying(20) NOT NULL,
                  "CreatedAt" timestamp with time zone NOT NULL)
              """;
        await _context.Database.ExecuteSqlRawAsync(editions, ct);

        var teams = Sqlite
            ? """
              CREATE TABLE IF NOT EXISTS "Teams" (
                  "Id" TEXT NOT NULL CONSTRAINT "PK_Teams" PRIMARY KEY,
                  "Name" TEXT NOT NULL,
                  "CreatedAt" TEXT NOT NULL)
              """
            : """
              CREATE TABLE IF NOT EXISTS "Teams" (
                  "Id" uuid NOT NULL CONSTRAINT "PK_Teams" PRIMARY KEY,
                  "Name" character varying(150) NOT NULL,
                  "CreatedAt" timestamp with time zone NOT NULL)
              """;
        await _context.Database.ExecuteSqlRawAsync(teams, ct);

        // Added with the editions screen
        if (Sqlite)
        {
            await AddSqliteColumnAsync("Editions", "StorageFolder", "TEXT NULL", ct);
            await AddSqliteColumnAsync("Editions", "ArchivedAt", "TEXT NULL", ct);
            await AddSqliteColumnAsync("Editions", "FinalStandingJson", "TEXT NULL", ct);
        }
        else
        {
            await _context.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE \"Editions\" ADD COLUMN IF NOT EXISTS \"StorageFolder\" character varying({Edition.MaxNameLength + 20}) NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Editions\" ADD COLUMN IF NOT EXISTS \"ArchivedAt\" timestamp with time zone NULL", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Editions\" ADD COLUMN IF NOT EXISTS \"FinalStandingJson\" text NULL", ct);
        }
    }

    /// <summary>
    /// SQLite: a copy of the whole database next to the files, as the conversion rebuilds a table. PostgreSQL has no
    /// such copy from here: the log says to make one first (the conversion runs in one transaction).
    /// </summary>
    private async Task BackUpBeforeEditionsAsync(CancellationToken ct)
    {
        if (!Sqlite)
        {
            _logger.LogWarning("Converting the database to editions; make a backup (pg_dump) before starting this version if you have none");
            return;
        }

        var folder = Path.Combine(Path.GetFullPath(_options.Value.StorageRoot), "_backup");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, $"pre-editions-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");
        // A path from the settings, not from input; quotes doubled for the SQL string
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync($"VACUUM INTO '{file.Replace("'", "''")}'", ct);
#pragma warning restore EF1002
        _logger.LogInformation("Saved a copy of the database before converting it to editions: {File}", file);
    }

    /// <summary>The edition the existing game becomes, named after the year its first submission came in.</summary>
    private async Task<Guid> FirstEditionIdAsync(CancellationToken ct)
    {
        var existing = await _context.Editions.IgnoreQueryFilters()
            .OrderBy(e => e.Status == EditionStatus.Active ? 0 : 1).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
        if (existing is { } id) return id;

        var created = await _context.Database
            .SqlQueryRaw<DateTimeOffset>("SELECT \"CreatedAt\" AS \"Value\" FROM \"Submissions\"").ToListAsync(ct);
        var year = created.Count > 0 ? created.Min().ToGameTime().Year : DateTimeOffset.UtcNow.ToGameTime().Year;
        var edition = new Edition { Name = Edition.DefaultName(year), Year = year, Status = EditionStatus.Active };
        _context.Editions.Add(edition);
        await _context.SaveChangesAsync(ct);
        return edition.Id;
    }

    private async Task AddEditionColumnsAsync(Guid edition, CancellationToken ct)
    {
        // How EF stores a Guid: upper-case text in SQLite, uuid in PostgreSQL. Always a Guid, never input.
        var value = Sqlite ? $"'{edition.ToString().ToUpperInvariant()}'" : $"'{edition}'::uuid";
        var type = Sqlite ? "TEXT" : "uuid";

#pragma warning disable EF1002
        if (!await ColumnExistsAsync("Groups", "EditionId", ct))
        {
            await AddColumnWithValueAsync("Groups", "EditionId", type, value, ct);
        }

        if (!await ColumnExistsAsync("Groups", "TeamId", ct))
        {
            await AddColumnWithValueAsync("Groups", "TeamId", type, value, ct);
            await _context.Database.ExecuteSqlRawAsync("UPDATE \"Groups\" SET \"TeamId\" = \"Id\"", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Teams\" (\"Id\", \"Name\", \"CreatedAt\") SELECT g.\"Id\", g.\"Name\", g.\"CreatedAt\" FROM \"Groups\" g " +
                "WHERE NOT EXISTS (SELECT 1 FROM \"Teams\" t WHERE t.\"Id\" = g.\"Id\")", ct);
        }

        if (!await ColumnExistsAsync("Assignments", "EditionId", ct))
        {
            await AddColumnWithValueAsync("Assignments", "EditionId", type, value, ct);
        }

        if (!await ColumnExistsAsync("Locations", "EditionId", ct))
        {
            if (Sqlite)
            {
                // SQLite cannot change a primary key: the table is built again
                await _context.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "Locations_new" (
                        "EditionId" TEXT NOT NULL,
                        "Number" INTEGER NOT NULL,
                        "Name" TEXT NOT NULL,
                        "Latitude" REAL NOT NULL,
                        "Longitude" REAL NOT NULL,
                        "Hint" TEXT NULL,
                        CONSTRAINT "PK_Locations" PRIMARY KEY ("EditionId", "Number"))
                    """, ct);
                await _context.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO \"Locations_new\" (\"EditionId\", \"Number\", \"Name\", \"Latitude\", \"Longitude\", \"Hint\") " +
                    $"SELECT {value}, \"Number\", \"Name\", \"Latitude\", \"Longitude\", \"Hint\" FROM \"Locations\"", ct);
                await _context.Database.ExecuteSqlRawAsync("DROP TABLE \"Locations\"", ct);
                await _context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Locations_new\" RENAME TO \"Locations\"", ct);
            }
            else
            {
                await AddColumnWithValueAsync("Locations", "EditionId", type, value, ct);
                await _context.Database.ExecuteSqlRawAsync("ALTER TABLE \"Locations\" DROP CONSTRAINT \"PK_Locations\"", ct);
                await _context.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"Locations\" ADD CONSTRAINT \"PK_Locations\" PRIMARY KEY (\"EditionId\", \"Number\")", ct);
            }
        }

        if (!Sqlite)
        {
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD CONSTRAINT \"FK_Groups_Editions_EditionId\" FOREIGN KEY (\"EditionId\") REFERENCES \"Editions\" (\"Id\") ON DELETE RESTRICT", ct);
            await _context.Database.ExecuteSqlRawAsync(
                "ALTER TABLE \"Groups\" ADD CONSTRAINT \"FK_Groups_Teams_TeamId\" FOREIGN KEY (\"TeamId\") REFERENCES \"Teams\" (\"Id\") ON DELETE RESTRICT", ct);
        }
#pragma warning restore EF1002
    }

    /// <summary>
    /// A new NOT NULL column, filled with the given value for the rows already there. PostgreSQL drops the default
    /// after; SQLite cannot, but the app always writes the column itself.
    /// </summary>
    private async Task AddColumnWithValueAsync(string table, string column, string type, string value, CancellationToken ct)
    {
#pragma warning disable EF1002
        await _context.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NOT NULL DEFAULT {value}", ct);
        if (!Sqlite)
        {
            await _context.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ALTER COLUMN \"{column}\" DROP DEFAULT", ct);
        }
#pragma warning restore EF1002
        _logger.LogInformation("Added column {Column} to {Table}", column, table);
    }

    /// <summary>
    /// A database without any edition (just made) gets the active edition of this year. The edition being played is
    /// then known to the rest of the app (the folder files go to).
    /// </summary>
    private async Task EnsureActiveEditionAsync(CancellationToken ct)
    {
        if (!await _context.Editions.AnyAsync(ct))
        {
            var year = DateTimeOffset.UtcNow.ToGameTime().Year;
            _context.Editions.Add(new Edition { Name = Edition.DefaultName(year), Year = year, Status = EditionStatus.Active });
            await _context.SaveChangesAsync(ct);
            _logger.LogInformation("Created the first edition: {Edition}", Edition.DefaultName(year));
        }

        var active = await _context.Editions.AsNoTracking().FirstOrDefaultAsync(e => e.Status == EditionStatus.Active, ct);
        if (active == null)
        {
            _logger.LogWarning("No edition is active: Slack messages find no group until an admin starts an edition");
            return;
        }

        _activeEdition?.Set(active);
    }
}
