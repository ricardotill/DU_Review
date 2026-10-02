using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ScoutingAutomation.Infrastructure.Data;
using ScoutingAutomation.Infrastructure.Repositories;
using Xunit;

namespace ScoutingAutomation.UnitTests.Data;

public class FailedSlackEventRepositoryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FailedSlackEventRepositoryTests()
    {
        _connection.Open();
        using var db = Context();
        db.Database.EnsureCreated();
    }

    private ScoutingDbContext Context(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<ScoutingDbContext>().UseSqlite(_connection).AddInterceptors(interceptors).Options);

    /// <summary>Lets the other failure of the same event land between this one's lookup and its save.</summary>
    private sealed class OtherFailureFirst(Func<Task> other) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_done)
            {
                _done = true;
                await other();
            }

            return result;
        }
    }

    [Fact]
    public async Task RecordFailureAsync_SameEventFailingTwiceAtOnce_ShouldCountBothOnOneRow()
    {
        await using var first = Context();
        await using var second = Context(new OtherFailureFirst(() =>
            new FailedSlackEventRepository(first, TimeProvider.System).RecordFailureAsync("Message:C1:1.0", "Message", "C1", "1.0", "{}", "eerste")));

        await new FailedSlackEventRepository(second, TimeProvider.System).RecordFailureAsync("Message:C1:1.0", "Message", "C1", "1.0", "{}", "tweede");

        await using var check = Context();
        var row = await check.FailedSlackEvents.SingleAsync();
        row.Attempts.Should().Be(2);
        row.Error.Should().Be("tweede");
    }

    public void Dispose() => _connection.Dispose();
}
