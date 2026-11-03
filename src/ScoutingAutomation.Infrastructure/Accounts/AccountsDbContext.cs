using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>
/// The account tables (AspNetUsers and friends), in the same database as <see cref="Data.ScoutingDbContext"/> but in
/// their own context: EnsureCreated does nothing on a database that already exists, so <see cref="AccountsInitializer"/>
/// creates these tables itself in a database from before accounts.
/// </summary>
public class AccountsDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public AccountsDbContext(DbContextOptions<AccountsDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(entity =>
        {
            entity.Property(u => u.DisplayName).IsRequired().HasMaxLength(AppUser.MaxDisplayNameLength);
            entity.HasIndex(u => u.GroupId);
        });
    }
}
