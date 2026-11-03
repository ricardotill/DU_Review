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
    private readonly ScoutingSettings _settings = new() { Accounts = { BootstrapAdminEmail = "admin@du.test" } };

    public AccountServiceTests()
    {
        var database = Guid.NewGuid().ToString();
        var services = new ServiceCollection()
            .AddLogging()
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
    public async Task BootstrapPassword_ShouldBeSetSoTheAdminCanLogInStraightAway()
    {
        _settings.Accounts.BootstrapAdminEmail = "second@du.test";
        _settings.Accounts.BootstrapAdminPassword = "een lang wachtwoord";
        await RemoveAdminRoleAsync();

        await InitializeAsync();

        var second = await With((_, users) => users.FindByEmailAsync("second@du.test"));
        (await With((_, users) => users.CheckPasswordAsync(second!, "een lang wachtwoord"))).Should().BeTrue();
    }

    [Theory]
    [InlineData(AccountKind.Jury, new[] { AccountRoles.Jury })]
    [InlineData(AccountKind.Admin, new[] { AccountRoles.Admin, AccountRoles.Jury })]
    [InlineData(AccountKind.Beamer, new[] { AccountRoles.Beamer })]
    [InlineData(AccountKind.Group, new[] { AccountRoles.Group })]
    public async Task Create_ShouldGiveTheRolesOfTheKind(AccountKind kind, string[] roles)
    {
        var group = kind == AccountKind.Group ? Guid.NewGuid() : (Guid?)null;

        var (user, error) = await With((service, _) => service.CreateAsync(" eva@du.test ", " Eva ", kind, group));

        error.Should().BeNull();
        user!.Email.Should().Be("eva@du.test");
        user.DisplayName.Should().Be("Eva");
        user.GroupId.Should().Be(group);
        (await With((_, users) => users.GetRolesAsync(user))).Should().BeEquivalentTo(roles);
    }

    [Fact]
    public async Task Create_ShouldRefuseMissingFieldsAndDuplicates()
    {
        (await With((s, _) => s.CreateAsync("", "Eva", AccountKind.Jury, null))).Error.Should().Be("Vul een e-mailadres in.");
        (await With((s, _) => s.CreateAsync("eva@du.test", " ", AccountKind.Jury, null))).Error.Should().Be("Vul een naam in.");
        (await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Group, null))).Error.Should().Be("Kies de groep van dit account.");
        (await With((s, _) => s.CreateAsync("geen-adres", "Eva", AccountKind.Jury, null))).Error.Should().Contain("geen geldig e-mailadres");

        await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));
        (await With((s, _) => s.CreateAsync("EVA@du.test", "Eva 2", AccountKind.Jury, null))).Error.Should().Contain("Er is al een account");
    }

    [Fact]
    public async Task LastAdmin_ShouldNotBeDemotedBlockedOrRemoved()
    {
        var admin = await AdminAsync();
        var (jury, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));

        (await With((s, _) => s.UpdateAsync(admin.Id, jury!.Id, "Beheerder", AccountKind.Jury, null))).Should().Be("Dit is de laatste beheerder.");
        (await With((s, _) => s.SetDisabledAsync(admin.Id, jury!.Id, true))).Should().Be("Dit is de laatste beheerder.");
        (await With((s, _) => s.DeleteAsync(admin.Id, jury!.Id))).Should().Be("Dit is de laatste beheerder.");
    }

    [Fact]
    public async Task Admin_ShouldNotDemoteBlockOrRemoveThemselves()
    {
        var admin = await AdminAsync();
        await With((s, _) => s.CreateAsync("second@du.test", "Tweede", AccountKind.Admin, null));

        (await With((s, _) => s.UpdateAsync(admin.Id, admin.Id, "Beheerder", AccountKind.Jury, null))).Should().Contain("jezelf");
        (await With((s, _) => s.SetDisabledAsync(admin.Id, admin.Id, true))).Should().Contain("jezelf");
        (await With((s, _) => s.DeleteAsync(admin.Id, admin.Id))).Should().Contain("eigen account");
    }

    [Fact]
    public async Task Update_ShouldChangeTheRolesAndEndTheSessions()
    {
        var admin = await AdminAsync();
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));
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
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));
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
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));
        var link = await With((s, _) => s.SendInvitationAsync(user!.Id, "https://du.test/"));
        link.Should().StartWith("https://du.test/wachtwoord-instellen?id=");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);

        var (set, error) = await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], "een lang wachtwoord"));

        error.Should().BeNull();
        set!.EmailConfirmed.Should().BeTrue();
        (await With((_, users) => users.CheckPasswordAsync(set, "een lang wachtwoord"))).Should().BeTrue();
        var (again, againError) = await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], "nog een wachtwoord"));
        again.Should().BeNull("a new password changes the security stamp, which ends the link");
        againError.Should().Contain("verlopen of al gebruikt");
    }

    [Fact]
    public async Task PasswordLink_ShouldRefuseAShortPasswordAndABrokenCode()
    {
        var (user, _) = await With((s, _) => s.CreateAsync("eva@du.test", "Eva", AccountKind.Jury, null));
        var link = await With((s, _) => s.SendInvitationAsync(user!.Id, "https://du.test"));
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(link).Query);

        (await With((s, _) => s.SetPasswordAsync(query["id"], query["code"], "kort"))).Error.Should().Be("Een wachtwoord heeft minstens 10 tekens.");
        (await With((s, _) => s.SetPasswordAsync(query["id"], "!!niet-base64!!", "een lang wachtwoord"))).Error.Should().Contain("verlopen");
        (await With((s, _) => s.SetPasswordAsync(Guid.NewGuid().ToString(), query["code"], "een lang wachtwoord"))).Error.Should().Contain("verlopen");
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
        await With((s, _) => s.CreateAsync("wolven@du.test", "De Wolven", AccountKind.Group, group));

        var list = await With((s, _) => s.ListAsync());

        list.Should().ContainSingle(a => a.Email == "wolven@du.test").Which.Should()
            .Match<AccountInfo>(a => a.Kind == AccountKind.Group && a.GroupId == group && !a.HasPassword && !a.IsDisabled);
        list.Should().ContainSingle(a => a.Email == "admin@du.test").Which.Kind.Should().Be(AccountKind.Admin);
    }

    private sealed class FakeMailer : IAccountMailer
    {
        public List<AccountMail> Sent { get; } = [];
        public bool CanSend => true;

        public Task SendAsync(AccountMail mail, CancellationToken ct = default)
        {
            Sent.Add(mail);
            return Task.CompletedTask;
        }
    }
}
