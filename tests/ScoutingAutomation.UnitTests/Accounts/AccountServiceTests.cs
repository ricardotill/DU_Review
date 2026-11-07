using Microsoft.Extensions.Logging;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Contracts.Configuration;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Settings;
using Xunit;

namespace ScoutingAutomation.UnitTests.Accounts;

/// <summary>Making and changing accounts, with the real Identity user manager on an in-memory database.</summary>
public sealed class AccountServiceTests : IAsyncLifetime, IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly FakeMailer _mailer = new();
    private readonly LogLines _log = new();
    private readonly ScoutingSettings _settings = new() { PublicUrl = "https://du.test", Accounts = { BootstrapAdminEmail = "admin@du.test" } };

    public AccountServiceTests()
    {
        var database = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddLogging(b => b.AddProvider(_log))
            .AddDataProtection().Services
            .AddDbContext<AccountsDbContext>(o => o.UseInMemoryDatabase(database))
            .AddSingleton<IOptions<ScoutingSettings>>(Options.Create(_settings))
            .AddSingleton<IAccountMailer>(_mailer)
            .AddSingleton<AccountChangeNotifier>()
            .AddScoped<AccountService>()
            .AddScoped<AccountsInitializer>();
        services.AddIdentityCore<AppUser>(o =>
            {
                o.User.RequireUniqueEmail = true;
                o.Password.RequiredLength = 10;
                o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AccountsDbContext>()
            .AddErrorDescriber<DutchIdentityErrorDescriber>()
            .AddDefaultTokenProviders();
        _services = services.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AccountsInitializer>().InitializeAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => DisposeAsync().AsTask();

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private async Task<T> With<T>(Func<AccountService, UserManager<AppUser>, Task<T>> action)
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AccountService>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    /// <summary>Made per run, so no password sits in the repository.</summary>
    private static readonly string NewPassword = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));

    private Task<bool> RemoveAdminRoleAsync() =>
        With(async (_, users) => (await users.RemoveFromRoleAsync((await users.FindByEmailAsync("admin@du.test"))!, AccountRoles.Admin)).Succeeded);

    private Task<AppUser> AdminAsync() => With((_, users) => users.FindByEmailAsync("admin@du.test"))!;

    [Fact]
    public async Task FirstStart_ShouldMakeTheAdminFromTheSettingsAndMailALink()
    {
        var admin = await AdminAsync();

        admin.Should().NotBeNull();
        (await With((_, users) => users.GetRolesAsync(admin))).Should().BeEquivalentTo([AccountRoles.Admin, AccountRoles.Jury]);
        admin.PasswordHash.Should().BeNull("the admin chooses the password through the link");
        _mailer.Sent.Should().ContainSingle(m => m.To == "admin@du.test" && m.Body.Contains("/wachtwoord-instellen?id="));
    }

    [Fact]
    public async Task WithoutAnAdminOrBootstrapAddress_ShouldStopTheStart()
    {
        _settings.Accounts.BootstrapAdminEmail = null;
        // The last admin cannot be demoted through the service: remove the role by hand, as if the database was edited
        await RemoveAdminRoleAsync();

        var act = async () =>
        {
            await using var scope = _services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AccountsInitializer>().InitializeAsync();
        };

        await act.Should().ThrowAsync<StartupConfigurationException>().WithMessage("*BOOTSTRAP_ADMIN_EMAIL*");
    }

    [Fact]
    public async Task WithoutAnAdminBootstrapAddressOrMailServer_ShouldMakeAdminWithPasswordAdmin()
    {
        _settings.Accounts.BootstrapAdminEmail = null;
        _mailer.CanSend = false;
        await RemoveAdminRoleAsync();

        await InitializeAsync();

        var admin = await With((_, users) => users.FindByNameAsync(AccountsInitializer.FallbackAdminName));
        admin.Should().NotBeNull();
        (await With((_, users) => users.GetRolesAsync(admin!))).Should().BeEquivalentTo([AccountRoles.Admin, AccountRoles.Jury]);
        (await With((_, users) => users.CheckPasswordAsync(admin!, "admin"))).Should().BeTrue();

        // A second start with the admin in place changes nothing
        await InitializeAsync();
        (await With((_, users) => Task.FromResult(users.Users.Count(u => u.UserName == "admin")))).Should().Be(1);
    }

    [Fact]
    public async Task BootstrapPassword_ShouldBeSetSoTheAdminCanLogInStraightAway()
    {
        _settings.Accounts.BootstrapAdminEmail = "second@du.test";
        _settings.Accounts.BootstrapAdminPassword = NewPassword;
        await RemoveAdminRoleAsync();

        await InitializeAsync();

        var second = await With((_, users) => users.FindByEmailAsync("second@du.test"));
        (await With((_, users) => users.CheckPasswordAsync(second!, NewPassword))).Should().BeTrue();
    }

    [Theory]
    [InlineData(AccountKind.Jury, new[] { AccountRoles.Jury })]
    [InlineData(AccountKind.Admin, new[] { AccountRoles.Admin, AccountRoles.Jury })]
    [InlineData(AccountKind.Beamer, new[] { AccountRoles.Beamer })]
    [InlineData(AccountKind.Group, new[] { AccountRoles.Group })]
    public async Task Create_ShouldGiveTheRolesOfTheKind(AccountKind kind, string[] roles)
    {
        var group = kind == AccountKind.Group ? Guid.NewGuid() : (Guid?)null;

        var (user, error) = await With((service, _) => service.CreateAsync(" eva@du.test ", " Eva ", kind, group, null));

        error.Should().BeNull();
        user!.Email.Should().Be("eva@du.test");
        user.DisplayName.Should().Be("Eva");
        user.GroupId.Should().Be(group);
        (await With((_, users) => users.GetRolesAsync(user))).Should().BeEquivalentTo(roles);
    }

    [Fact]
    public async Task Create_ShouldRefuseMissingFieldsAndDuplicates()
    {
        (await With((s, _) => s.CreateAsync("", "Eva", AccountKind.Jury, null, null))).Error.Should().Be("Vul een e-mailadres in.");
        (await With((s, _) => s.CreateAsync("eva@du.test", " ", AccountKind.Jury, null, null))).Error.Should().Be("Vul een naam in.");
        (await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Group, null, null))).Error.Should().Be("Kies de groep van dit account.");
        (await With((s, _) => s.CreateAsync("geen-adres", "Eva", AccountKind.Jury, null, null))).Error.Should().Contain("geen geldig e-mailadres");

        await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        (await With((s, _) => s.CreateAsync("EVA@du.test", "Eva 2", AccountKind.Jury, null, null))).Error.Should().Contain("Er is al een account");
    }

    [Fact]
    public async Task NonAdmin_ShouldNotChangeAccounts_EvenWithAStaleAdminLogin()
    {
        // As if a tab still had the admin role in its login while the account was just demoted: the database decides
        var admin = await AdminAsync();
        var (jury, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));

        (await With((s, _) => s.UpdateAsync(admin.Id, jury!.Id, "Beheerder", AccountKind.Jury, null))).Should().Be(AccountService.NotAdminMessage);
        (await With((s, _) => s.SetDisabledAsync(admin.Id, jury!.Id, true))).Should().Be(AccountService.NotAdminMessage);
        (await With((s, _) => s.DeleteAsync(admin.Id, jury!.Id))).Should().Be(AccountService.NotAdminMessage);
        (await With((s, _) => s.CreateAsync("nieuw@du.test", "Nieuw", AccountKind.Admin, null, jury!.Id))).Error.Should().Be(AccountService.NotAdminMessage);
        var invite = () => With((s, _) => s.SendInvitationAsync(admin.Id, "https://du.test", jury!.Id));
        await invite.Should().ThrowAsync<InvalidOperationException>().WithMessage(AccountService.NotAdminMessage);
        (await With((_, users) => users.FindByEmailAsync("nieuw@du.test"))).Should().BeNull();
    }

    [Fact]
    public async Task TwoAdmins_BlockingEachOtherAtOnce_ShouldLeaveOneAdmin()
    {
        var first = await AdminAsync();
        var (second, _) = await With((s, _) => s.CreateAsync("tweede@du.test", "Tweede", AccountKind.Admin, null, null));

        var results = await Task.WhenAll(
            Task.Run(() => With((s, _) => s.SetDisabledAsync(second!.Id, first.Id, true))),
            Task.Run(() => With((s, _) => s.SetDisabledAsync(first.Id, second!.Id, true))));

        // Whoever comes second is blocked already, so no admin any more
        results.Should().ContainSingle(r => r == null).And.ContainSingle(r => r == AccountService.NotAdminMessage);
    }

    [Fact]
    public async Task Admin_ShouldNotDemoteBlockOrRemoveThemselves()
    {
        var admin = await AdminAsync();
        await With((s, _) => s.CreateAsync("second@du.test", "Tweede", AccountKind.Admin, null, null));

        (await With((s, _) => s.UpdateAsync(admin.Id, admin.Id, "Beheerder", AccountKind.Jury, null))).Should().Contain("jezelf");
        (await With((s, _) => s.SetDisabledAsync(admin.Id, admin.Id, true))).Should().Contain("jezelf");
        (await With((s, _) => s.DeleteAsync(admin.Id, admin.Id))).Should().Contain("eigen account");
    }

    [Fact]
    public async Task Update_ShouldChangeTheRolesAndEndTheSessions()
    {
        var admin = await AdminAsync();
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        var changed = new List<Guid>();
        _services.GetRequiredService<AccountChangeNotifier>().Changed += changed.Add;

        var error = await With((s, _) => s.UpdateAsync(user!.Id, admin.Id, "Eva B", AccountKind.Beamer, null));

        error.Should().BeNull();
        var after = await With((_, users) => users.FindByIdAsync(user!.Id.ToString()));
        after!.DisplayName.Should().Be("Eva B");
        after.SecurityStamp.Should().NotBe(user!.SecurityStamp, "open logins of this account must end");
        (await With((_, users) => users.GetRolesAsync(after))).Should().BeEquivalentTo([AccountRoles.Beamer]);
        changed.Should().Contain(user.Id);
    }

    [Fact]
    public async Task BlockedAccount_ShouldBeLockedOutAndGetNoResetMail()
    {
        var admin = await AdminAsync();
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        _mailer.Sent.Clear();

        await With((s, _) => s.SetDisabledAsync(user!.Id, admin.Id, true));
        await With(async (s, _) => { await s.SendPasswordResetAsync("eva@du.test", "https://du.test"); return 0; });

        var blocked = await With((_, users) => users.FindByIdAsync(user!.Id.ToString()));
        blocked!.IsDisabled.Should().BeTrue();
        (await With((_, users) => users.IsLockedOutAsync(blocked))).Should().BeTrue();
        _mailer.Sent.Should().BeEmpty();

        await With((s, _) => s.SetDisabledAsync(user!.Id, admin.Id, false));
        (await With((_, users) => users.IsLockedOutAsync(user!))).Should().BeFalse();
    }

    [Fact]
    public async Task PasswordLink_ShouldSetThePasswordOnceAndConfirmTheAddress()
    {
        var admin = await AdminAsync();
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        var link = await With((s, _) => s.SendInvitationAsync(user!.Id, "https://du.test/", admin.Id));
        link.Should().StartWith("https://du.test/wachtwoord-instellen?id=");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);

        var (set, error) = await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], NewPassword));

        error.Should().BeNull();
        set!.EmailConfirmed.Should().BeTrue();
        (await With((_, users) => users.CheckPasswordAsync(set, NewPassword))).Should().BeTrue();
        var (again, againError) = await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], "nog een wachtwoord"));
        again.Should().BeNull("a new password changes the security stamp, which ends the link");
        againError.Should().Contain("verlopen of al gebruikt");
    }

    [Fact]
    public async Task PasswordLink_ShouldRefuseAShortPasswordAndABrokenCode()
    {
        var admin = await AdminAsync();
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        var link = await With((s, _) => s.SendInvitationAsync(user!.Id, "https://du.test", admin.Id));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);

        (await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], "kort"))).Error.Should().Be("Een wachtwoord heeft minstens 10 tekens.");
        (await With((s, _) => s.SetPasswordAsync(query["id"], "!!niet-base64!!", NewPassword))).Error.Should().Contain("verlopen");
        (await With((s, _) => s.SetPasswordAsync(Guid.NewGuid().ToString(), query["code"], NewPassword))).Error.Should().Contain("verlopen");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("javascript:alert(1)")]
    public async Task PasswordReset_WithoutThePublicUrl_ShouldSendNothing(string? baseUrl)
    {
        // The page passes Scouting:PublicUrl only; never the request's own address, which a forged Host header controls
        await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null, null));
        _mailer.Sent.Clear();

        await With(async (s, _) => { await s.SendPasswordResetAsync("eva@du.test", baseUrl); return 0; });

        _mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task FirstStart_WithMailButNoPublicUrl_ShouldLogTheLinkInsteadOfMailingABarePath()
    {
        _settings.PublicUrl = null;
        _settings.Accounts.BootstrapAdminEmail = "second@du.test";
        await RemoveAdminRoleAsync();

        await InitializeAsync();

        _mailer.Sent.Should().NotContain(m => m.To == "second@du.test");
        _log.Lines.Should().Contain(l => l.Contains("second@du.test") && l.Contains("/wachtwoord-instellen?id="));
    }

    [Fact]
    public async Task FirstStart_WithMail_ShouldNotLogTheLink()
    {
        _log.Lines.Should().NotContain(l => l.Contains("wachtwoord-instellen"), "the link went by mail; a log is read by more people");
    }

    [Fact]
    public async Task FirstStart_WithoutMail_ShouldLogTheLinkAsTheOnlyWayIn()
    {
        _settings.Accounts.BootstrapAdminEmail = "second@du.test";
        _mailer.CanSend = false;
        await RemoveAdminRoleAsync();

        await InitializeAsync();

        _log.Lines.Should().Contain(l => l.Contains("second@du.test") && l.Contains("/wachtwoord-instellen?id="));
        _mailer.Sent.Should().NotContain(m => m.To == "second@du.test");
    }

    [Fact]
    public async Task PasswordReset_ForAnUnknownAddress_ShouldSendNothing()
    {
        _mailer.Sent.Clear();

        await With(async (s, _) => { await s.SendPasswordResetAsync("niemand@du.test", "https://du.test"); return 0; });

        _mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task List_ShouldShowKindGroupAndWhetherAPasswordIsSet()
    {
        var group = Guid.NewGuid();
        await With((s, _) => s.CreateAsync("wolven@du.test", "De Wolven", AccountKind.Group, group, null));

        var list = await With((s, _) => s.ListAsync());

        list.Should().ContainSingle(a => a.Email == "wolven@du.test").Which.Should()
            .Match<AccountInfo>(a => a.Kind == AccountKind.Group && a.GroupId == group && !a.HasPassword && !a.IsDisabled);
        list.Should().ContainSingle(a => a.Email == "admin@du.test").Which.Kind.Should().Be(AccountKind.Admin);
    }

    private sealed class FakeMailer : IAccountMailer
    {
        public List<AccountMail> Sent { get; } = [];
        public bool CanSend { get; set; } = true;

        public Task SendAsync(AccountMail mail, CancellationToken ct = default)
        {
            if (!CanSend) return Task.CompletedTask;
            Sent.Add(mail);
            return Task.CompletedTask;
        }
    }

    private sealed class LogLines : ILoggerProvider, ILogger
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) return _lines.ToList(); } }

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines) _lines.Add(formatter(state, exception));
        }

        public void Dispose() { }
    }
}
