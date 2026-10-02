using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ScoutingAutomation.Infrastructure.Data;

public static class DbErrors
{
    /// <summary>A save refused by a unique index, on SQLite or PostgreSQL.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex) => ex.InnerException switch
    {
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode == 2067, // SQLITE_CONSTRAINT_UNIQUE
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
        _ => false
    };
}
