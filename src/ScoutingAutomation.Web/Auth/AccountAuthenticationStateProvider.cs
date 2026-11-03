using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Infrastructure.Accounts;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// The login state of an open browser tab. A tab stays open for hours, so its login is checked again every minute and
/// at once when an admin changes, blocks or removes an account: the tab then goes to the login page.
/// </summary>
public sealed class AccountAuthenticationStateProvider : ServerAuthenticationStateProvider, IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly AccountChangeNotifier _notifier;
    private readonly IdentityOptions _options;
    private readonly ILogger<AccountAuthenticationStateProvider> _logger;
    private readonly CancellationTokenSource _stop = new();

    public AccountAuthenticationStateProvider(
        IServiceScopeFactory scopes,
        AccountChangeNotifier notifier,
        IOptions<IdentityOptions> options,
        ILogger<AccountAuthenticationStateProvider> logger)
    {
        _scopes = scopes;
        _notifier = notifier;
        _options = options.Value;
        _logger = logger;
        _notifier.Changed += OnAccountChanged;
        _ = RevalidateLoopAsync(_stop.Token);
    }

    private void OnAccountChanged(Guid userId) => _ = RevalidateAsync(userId);

    private async Task RevalidateLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RevalidateAsync(null);
            }
        }
        catch (OperationCanceledException)
        {
            // The tab was closed
        }
    }

    /// <summary>Checks the login of this tab against its account; only when it is <paramref name="changedUser"/> if given.</summary>
    public async Task RevalidateAsync(Guid? changedUser)
    {
        AuthenticationState state;
        try
        {
            state = await GetAuthenticationStateAsync();
        }
        catch (InvalidOperationException)
        {
            // No login state yet: nothing to withdraw
            return;
        }

        if (state.User.Identity?.IsAuthenticated != true) return;
        if (changedUser != null && AppAuth.UserId(state.User) != changedUser) return;

        try
        {
            var validated = await ValidateAsync(state.User);
            if (!ReferenceEquals(validated, state.User))
            {
                SetAuthenticationState(Task.FromResult(new AuthenticationState(validated)));
            }
        }
        catch (Exception ex)
        {
            // A database that is briefly away must not log anyone out; the next check tries again
            _logger.LogWarning(ex, "Could not check the login of an open tab");
        }
    }

    /// <returns>The same principal while the account is unchanged, otherwise an empty one: the login has ended.</returns>
    /// <remarks>
    /// Any change an admin makes (role, group, block) and a new password renew the security stamp. Like the cookie check
    /// (SecurityStampValidator), a changed stamp ends the login, so the next login gets the new rights.
    /// </remarks>
    private async Task<ClaimsPrincipal> ValidateAsync(ClaimsPrincipal user)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var account = AppAuth.UserId(user) is { } id ? await users.FindByIdAsync(id.ToString()) : null;
        var stamp = user.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
        return account == null || account.IsDisabled || stamp != account.SecurityStamp
            ? new ClaimsPrincipal(new ClaimsIdentity())
            : user;
    }

    public void Dispose()
    {
        _notifier.Changed -= OnAccountChanged;
        _stop.Cancel();
        _stop.Dispose();
    }
}
