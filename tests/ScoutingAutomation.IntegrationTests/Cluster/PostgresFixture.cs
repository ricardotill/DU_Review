using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace ScoutingAutomation.IntegrationTests.Cluster;

/// <summary>
/// A real PostgreSQL in a container, for the tests of several instances on one database. Without Docker those tests are
/// skipped (CI has Docker, so there they always run).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public string? Unavailable { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await _container.StartAsync();
        }
        catch (Exception ex)
        {
            Unavailable = $"No PostgreSQL container: {ex.Message}";
        }
    }

    /// <summary>A new, empty database on the server, so tests do not see each other's data.</summary>
    public async Task<string> NewDatabaseAsync()
    {
        Skip.If(Unavailable != null, Unavailable);
        var name = $"du_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_container!.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        if (_container != null) await _container.DisposeAsync();
    }
}

// Not side by side with other tests: the two-instance tests set the process-wide connection string, as Aspire does
[CollectionDefinition(nameof(PostgresCollection), DisableParallelization = true)]
public class PostgresCollection : ICollectionFixture<PostgresFixture>;
