using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>
/// The keys that sign the login cookie and the password links. In the database, so every instance of the app accepts the
/// cookie of the others, and a new container does not log everyone out. A context of its own, so the table can be added to
/// a database that already has its account tables, and the other way round. AccountsInitializer makes the table.
/// </summary>
public class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
}
