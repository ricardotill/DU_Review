using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Settings;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>
/// Runs at startup after <see cref="Data.DatabaseInitializer"/>: creates the account tables in a database from before
/// accounts, the roles, and the first admin from <see cref="AccountSettings.BootstrapAdminEmail"/>.
/// </summary>
public class AccountsInitializer
{
    private readonly AccountsDbContext _context;
    private readonly UserManager<AppUser> _users;
    private readonly RoleManager<IdentityRole<Guid>> _roles;
    private readonly AccountService _accounts;
    private readonly IAccountMailer _mailer;
    private readonly IOptions<ScoutingSettings> _options;
    private readonly ILogger<AccountsInitializer> _logger;

    public AccountsInitializer(
        AccountsDbContext context,
        UserManager<AppUser> users,
        RoleManager<IdentityRole<Guid>> roles,
        AccountService accounts,
        IAccountMailer mailer,
        IOptions<ScoutingSettings> options,
        ILogger<AccountsInitializer> logger)
    {
        _context = context;
        _users = users;
        _roles = roles;
        _accounts = accounts;
        _mailer = mailer;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsureTablesAsync(ct);

        foreach (var role in AccountRoles.All)
        {
            if (!await _roles.RoleExistsAsync(role))
            {
                await _roles.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        await EnsureAdminAsync();
    }

    private async Task EnsureTablesAsync(CancellationToken ct)
    {
        if (!_context.Database.IsRelational())
        {
            await _context.Database.EnsureCreatedAsync(ct);
            return;
        }

        var sql = _context.Database.IsSqlite()
            ? "SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type = 'table' AND name = 'AspNetUsers'"
            : "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema = current_schema() AND table_name = 'AspNetUsers'";
        var exists = await _context.Database.SqlQueryRaw<int>(sql).SingleAsync(ct) > 0;
        if (exists) return;

        // The database itself exists (DatabaseInitializer made or found it); only the account tables are missing
        await _context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(ct);
        _logger.LogInformation("Account tables created");
    }

    private async Task EnsureAdminAsync()
    {
        var admins = await _users.GetUsersInRoleAsync(AccountRoles.Admin);
        if (admins.Any(a => !a.IsDisabled)) return;

        var settings = _options.Value.Accounts;
        var email = settings.BootstrapAdminEmail?.Trim();
        if (string.IsNullOrEmpty(email))
        {
            throw new StartupConfigurationException(
                "Er is nog geen beheerder. Zet Scouting:Accounts:BootstrapAdminEmail (in .env: BOOTSTRAP_ADMIN_EMAIL) op je e-mailadres en start opnieuw.");
        }

        var user = await _users.FindByEmailAsync(email);
        if (user == null)
        {
            var (created, error) = await _accounts.CreateAsync(email, "Beheerder", AccountKind.Admin, null, actingUserId: null);
            user = created ?? throw new StartupConfigurationException($"De eerste beheerder ({email}) kon niet worden gemaakt: {error}");
        }
        else
        {
            // An existing account named in the settings becomes admin again, e.g. after the last admin was blocked by hand in the database
            user.IsDisabled = false;
            user.LockoutEnd = null;
            await _users.UpdateAsync(user);
            foreach (var role in AccountKinds.RolesOf(AccountKind.Admin))
            {
                if (!await _users.IsInRoleAsync(user, role)) await _users.AddToRoleAsync(user, role);
            }
        }

        if (!string.IsNullOrEmpty(settings.BootstrapAdminPassword) && user.PasswordHash == null)
        {
            var result = await _users.AddPasswordAsync(user, settings.BootstrapAdminPassword);
            if (!result.Succeeded)
            {
                throw new StartupConfigurationException($"Scouting:Accounts:BootstrapAdminPassword klopt niet: {AccountService.Describe(result)}");
            }

            _logger.LogWarning("First admin {Email} made with the password from the settings", email);
            return;
        }

        var publicUrl = _options.Value.PublicUrl?.TrimEnd('/');
        // A mailed link needs the full address; without PublicUrl it would be a bare path nobody can click
        var absolute = Uri.TryCreate(publicUrl, UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp);
        var link = await _accounts.CreatePasswordLinkAsync(user, publicUrl ?? "");
        if (_mailer.CanSend && absolute)
        {
            try
            {
                await _mailer.SendAsync(AccountMails.Invitation(user, link));
                _logger.LogWarning("First admin {Email}: a link to choose a password was mailed", email);
                return;
            }
            catch (AccountMailException)
            {
                // Logged by the mailer; fall back to the log below
            }
        }

        // Only without working mail: then the log is the only way in on a fresh install. The link works once, for a few days,
        // and anyone who can read this log already controls the server.
        _logger.LogWarning("First admin {Email}: no mail sent (no mail server or no Scouting:PublicUrl), choose a password at {Link}", email, link);
    }
}
