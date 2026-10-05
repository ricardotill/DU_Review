using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Storage;
using Xunit;

namespace ScoutingAutomation.UnitTests.Data;

public class SlackFileIdMigrationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public async Task Initialize_DatabaseFromBeforeFileIds_ShouldAddTheColumnAndUniqueIndex()
    {
        var file = Path.Combine(_temp.Path, "old.db");
        var options = new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite($"Data Source={file};Pooling=False").Options;
        await using (var old = new ScoutingDbContext(options))
        {
            await old.Database.EnsureCreatedAsync();
            await old.Database.ExecuteSqlRawAsync($"DROP INDEX \"{ScoutingDbContext.UniqueFileIndexName}\"");
            await old.Database.ExecuteSqlRawAsync("ALTER TABLE \"Submissions\" DROP COLUMN \"SlackFileId\"");
        }

        var settings = new ScoutingSettings { StorageRoot = Path.Combine(_temp.Path, "storage"), AssignmentPointsFile = Path.Combine(_temp.Path, "none.csv") };
        await using (var db = new ScoutingDbContext(options))
        {
            await new DatabaseInitializer(db, new LocalFileStorageService(Options.Create(settings), NullLogger<LocalFileStorageService>.Instance),
                Options.Create(settings), NullLogger<DatabaseInitializer>.Instance).InitializeAsync();
        }

        await using var check = new SqliteConnection($"Data Source={file};Pooling=False");
        await check.OpenAsync();
        Scalar(check, "SELECT COUNT(*) FROM pragma_table_info('Submissions') WHERE name = 'SlackFileId'").Should().Be(1);
        Scalar(check, $"SELECT \"unique\" FROM pragma_index_list('Submissions') WHERE name = '{ScoutingDbContext.UniqueFileIndexName}'").Should().Be(1);
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose() => _temp.Dispose();
}
