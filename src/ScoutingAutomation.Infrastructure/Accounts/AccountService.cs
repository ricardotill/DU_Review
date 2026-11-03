using ScoutingAutomation.Infrastructure.Cluster;
using System.Buffers.Text;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ScoutingAutomation.Infrastructure.Accounts;

/// <summary>An account as the account list shows it.</summary>
public sealed record AccountInfo(
    Guid Id,
    string Email,
    string DisplayName,
    AccountKind Kind,
    Guid? GroupId,
    bool HasPassword,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// Creating and changing accounts. Admins make every account; nobody can register. A new account gets no password:
/// its owner chooses one through a link, mailed by <see cref="IAccountMailer"/> (or copied by the admin).
/// </summary>
public class AccountService
{
    /// <summary>How long an invitation or password link works; the token lifespan is set to this in Program.cs.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromDays(3);

    public const string LinkLifetimeText = "3 dagen";

    /// <summary>The page the links lead to.</summary>
    public const string SetPasswordPath = "/wachtwoord-instellen";

    private readonly UserManager<AppUser> _users;
    private readonly IAccountMailer _mailer;
    private readonly AccountChangeNotifier _notifier;
    private readonly ILogger<AccountService> _logger;

    public AccountService(UserManager<AppUser> users, IAccountMailer mailer, AccountChangeNotifier notifier, ILogger<AccountService> logger,
        IClusterLocks? cluster = null)
    {
        _cluster = cluster ?? SingleInstance.Instance;
        _users = users;
        _mailer = mailer;
        _notifier = notifier;
        _logger = logger;
    }

    public bool CanSendMail => _mailer.CanSend;

    public async Task<IReadOnlyList<AccountInfo>> ListAsync(CancellationToken ct = default)
    {
        var users = await _users.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct);
        var result = new List<AccountInfo>(users.Count);
        foreach (var user in users)
        {
            result.Add(ToInfo(user, AccountKinds.FromRoles(await _users.GetRolesAsync(user))));
        }

        return result;
    }

    /// <returns>The new account, or the reason it was not made.</returns>
    /// <param name="actingUserId">The admin who makes it, checked against the database; null only for the app itself (the first admin).</param>
    public Task<(AppUser? User, string? Error)> CreateAsync(string? email, string? displayName, AccountKind kind, Guid? groupId, Guid? actingUserId) =>
        // One at a time with the changes that take away an admin: an admin who is demoted or blocked at the same
        // moment, also on another instance, cannot still make an account afterwards
        _cluster.OneAtATimeAsync(AdminChanges, ClusterLockNames.Accounts, () => CreateCoreAsync(email, displayName, kind, groupId, actingUserId));

    private async Task<(AppUser? User, string? Error)> CreateCoreAsync(string? email, string? displayName, AccountKind kind, Guid? groupId, Guid? actingUserId)
    {
        if (actingUserId is { } acting && await NotAdminAsync(acting) is { } notAdmin) return (null, notAdmin);
        email = email?.Trim();
        displayName = displayName?.Trim();
        if (string.IsNullOrEmpty(email)) return (null, "Vul een e-mailadres in.");
        if (string.IsNullOrEmpty(displayName)) return (null, "Vul een naam in.");
        if (displayName.Length > AppUser.MaxDisplayNameLength) return (null, $"Een naam is hooguit {AppUser.MaxDisplayNameLength} tekens.");
        if (kind == AccountKind.Group && groupId == null) return (null, "Kies de groep van dit account.");

        var user = new AppUser
        {
            UserName = email,
            Email = email,
            DisplayName = displayName,
            GroupId = kind == AccountKind.Group ? groupId : null
        };

        var created = await _users.CreateAsync(user);
        if (!created.Succeeded) return (null, Describe(created));

        var roles = await _users.AddToRolesAsync(user, AccountKinds.RolesOf(kind));
        if (!roles.Succeeded)
        {
            await _users.DeleteAsync(user);
            return (null, Describe(roles));
        }

        _logger.LogInformation("Account {Email} made as {Kind}", email, kind);
        return (user, null);
    }

    /// <returns>Null when done, otherwise the reason it was not changed.</returns>
    public Task<string?> UpdateAsync(Guid id, Guid actingUserId, string? displayName, AccountKind kind, Guid? groupId) =>
        OneAtATimeAsync(() => UpdateCoreAsync(id, actingUserId, displayName, kind, groupId));

    private async Task<string?> UpdateCoreAsync(Guid id, Guid actingUserId, string? displayName, AccountKind kind, Guid? groupId)
    {
        if (await NotAdminAsync(actingUserId) is { } notAdmin) return notAdmin;
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return "Dit account bestaat niet meer.";

        displayName = displayName?.Trim();
        if (string.IsNullOrEmpty(displayName)) return "Vul een naam in.";
        if (displayName.Length > AppUser.MaxDisplayNameLength) return $"Een naam is hooguit {AppUser.MaxDisplayNameLength} tekens.";
        if (kind == AccountKind.Group && groupId == null) return "Kies de groep van dit account.";

        var currentRoles = await _users.GetRolesAsync(user);
        var currentKind = AccountKinds.FromRoles(currentRoles);
        if (currentKind == AccountKind.Admin && kind != AccountKind.Admin)
        {
            if (id == actingUserId) return "Je kunt jezelf geen beheerder af maken. Laat een andere beheerder dat doen.";
            if (await IsLastActiveAdminAsync(user)) return "Dit is de laatste beheerder.";
        }

        // Roles before the group: if the roles fail, nothing changed. A group role without a group is refused everywhere
        // (no group claim), so a failure in between never opens up other groups' data.
        if (currentKind != kind)
        {
            var removed = await _users.RemoveFromRolesAsync(user, currentRoles);
            var added = removed.Succeeded ? await _users.AddToRolesAsync(user, AccountKinds.RolesOf(kind)) : removed;
            if (!added.Succeeded) return Describe(added);
        }

        user.DisplayName = displayName;
        user.GroupId = kind == AccountKind.Group ? groupId : null;
        var updated = await _users.UpdateAsync(user);
        if (!updated.Succeeded) return Describe(updated);

        // Ends the account's open sessions, so its new rights (or group) count at once
        await _users.UpdateSecurityStampAsync(user);
        _notifier.Notify(id);
        _logger.LogInformation("Account {Email} changed to {Kind}", user.Email, kind);
        return null;
    }

    /// <returns>Null when done, otherwise the reason it was not changed.</returns>
    public Task<string?> SetDisabledAsync(Guid id, Guid actingUserId, bool disabled) =>
        OneAtATimeAsync(() => SetDisabledCoreAsync(id, actingUserId, disabled));

    private async Task<string?> SetDisabledCoreAsync(Guid id, Guid actingUserId, bool disabled)
    {
        if (await NotAdminAsync(actingUserId) is { } notAdmin) return notAdmin;
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return "Dit account bestaat niet meer.";
        if (disabled && id == actingUserId) return "Je kunt jezelf niet blokkeren.";
        if (disabled && await IsLastActiveAdminAsync(user)) return "Dit is de laatste beheerder.";

        user.IsDisabled = disabled;
        // Lockout is what stops the login itself; IsDisabled tells it apart from a lockout after wrong passwords
        user.LockoutEnabled = true;
        user.LockoutEnd = disabled ? DateTimeOffset.MaxValue : null;
        var result = await _users.UpdateAsync(user);
        if (!result.Succeeded) return Describe(result);
        if (!disabled) await _users.ResetAccessFailedCountAsync(user);

        await _users.UpdateSecurityStampAsync(user);
        _notifier.Notify(id);
        _logger.LogWarning("Account {Email} {State}", user.Email, disabled ? "blocked" : "unblocked");
        return null;
    }

    /// <returns>Null when done, otherwise the reason it was not removed.</returns>
    public Task<string?> DeleteAsync(Guid id, Guid actingUserId) =>
        OneAtATimeAsync(() => DeleteCoreAsync(id, actingUserId));

    private async Task<string?> DeleteCoreAsync(Guid id, Guid actingUserId)
    {
        if (await NotAdminAsync(actingUserId) is { } notAdmin) return notAdmin;
        var user = await _users.FindByIdAsync(id.ToString());
        if (user == null) return null;
        if (id == actingUserId) return "Je kunt je eigen account niet verwijderen.";
        if (await IsLastActiveAdminAsync(user)) return "Dit is de laatste beheerder.";

        var result = await _users.DeleteAsync(user);
        if (!result.Succeeded) return Describe(result);

        _notifier.Notify(id);
        _logger.LogWarning("Account {Email} removed", user.Email);
        return null;
    }

    /// <summary>A link that lets the account's owner choose a (new) password, valid for <see cref="LinkLifetime"/>.</summary>
    public async Task<string> CreatePasswordLinkAsync(AppUser user, string baseUrl)
    {
        var token = await _users.GeneratePasswordResetTokenAsync(user);
        var code = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(token));
        return $"{baseUrl.TrimEnd('/')}{SetPasswordPath}?id={user.Id}&code={code}";
    }

    /// <summary>Mails the invitation to choose a password.</summary>
    /// <returns>The link, so the admin can pass it on when no mail could be sent.</returns>
    /// <exception cref="AccountMailException">The mail could not be sent.</exception>
    /// <exception cref="InvalidOperationException">The acting user is no admin (any more), or the account is gone.</exception>
    public async Task<string> SendInvitationAsync(Guid id, string baseUrl, Guid actingUserId)
    {
        if (await NotAdminAsync(actingUserId) is { } notAdmin) throw new InvalidOperationException(notAdmin);
        var user = await _users.FindByIdAsync(id.ToString()) ?? throw new InvalidOperationException("Dit account bestaat niet meer.");
        var link = await CreatePasswordLinkAsync(user, baseUrl);
        await _mailer.SendAsync(AccountMails.Invitation(user, link));
        return link;
    }

    /// <summary>Mails a link to choose a new password; does nothing for an unknown or blocked address.</summary>
    /// <param name="baseUrl">
    /// The configured public address of the app. Never the address of the request: anyone can ask for a reset, and a
    /// forged Host header would otherwise put the link, and so the token, on the asker's site.
    /// </param>
    /// <remarks>The page always shows the same message, so it does not tell which addresses have an account.</remarks>
    public async Task SendPasswordResetAsync(string? email, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicUrl) || (publicUrl.Scheme != Uri.UriSchemeHttps && publicUrl.Scheme != Uri.UriSchemeHttp))
        {
            _logger.LogWarning("Password reset asked, but Scouting:PublicUrl is not set: no link mailed. An admin can make one on Beheer");
            return;
        }

        var user = await _users.FindByEmailAsync(email.Trim());
        if (user == null || user.IsDisabled)
        {
            _logger.LogInformation("Password reset asked for unknown or blocked address");
            return;
        }

        var link = await CreatePasswordLinkAsync(user, baseUrl!.TrimEnd('/'));
        try
        {
            await _mailer.SendAsync(AccountMails.PasswordReset(user, link));
        }
        catch (AccountMailException)
        {
            // Logged by the mailer; the visitor gets the same message either way
        }
    }

    /// <summary>Sets a password through a link from <see cref="CreatePasswordLinkAsync"/>; also confirms the address.</summary>
    /// <returns>The account on success, otherwise the reason.</returns>
    public async Task<(AppUser? User, string? Error)> SetPasswordAsync(string? id, string? code, string? password)
    {
        const string BadLink = "Deze link is verlopen of al gebruikt. Vraag een nieuwe aan via \"Wachtwoord vergeten\".";
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(code)) return (null, BadLink);

        var user = await _users.FindByIdAsync(id);
        if (user == null || user.IsDisabled) return (null, BadLink);

        string token;
        try
        {
            token = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(code));
        }
        catch (FormatException)
        {
            return (null, BadLink);
        }

        var result = await _users.ResetPasswordAsync(user, token, password ?? "");
        if (!result.Succeeded)
        {
            return (null, result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)) ? BadLink : Describe(result));
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await _users.UpdateAsync(user);
        }

        // Wrong passwords from before no longer count against the new one
        await _users.ResetAccessFailedCountAsync(user);
        _notifier.Notify(user.Id);
        _logger.LogInformation("Password set for {Email}", user.Email);
        return (user, null);
    }

    /// <exception cref="AccountMailException">The mail could not be sent.</exception>
    public Task SendTestMailAsync(string to) => _mailer.SendAsync(AccountMails.Test(to));

    public static string Describe(IdentityResult result) => string.Join(" ", result.Errors.Select(e => e.Description));

    /// <summary>
    /// Changes that can take away an admin run one at a time, so two admins who demote, block or delete each other at the same
    /// moment cannot both pass the last-admin check. Also across instances of the app on the same database.
    /// </summary>
    private static readonly SemaphoreSlim AdminChanges = new(1, 1);

    private readonly IClusterLocks _cluster;

    private Task<string?> OneAtATimeAsync(Func<Task<string?>> change) =>
        _cluster.OneAtATimeAsync(AdminChanges, ClusterLockNames.Accounts, change);

    /// <summary>
    /// Whether <paramref name="actingUserId"/> is an active admin right now, read from the database. A page checks the
    /// login's roles too, but those can lag behind for a moment after the account was changed.
    /// </summary>
    /// <returns>Null for an admin, otherwise the reason.</returns>
    private async Task<string?> NotAdminAsync(Guid actingUserId)
    {
        var acting = await _users.FindByIdAsync(actingUserId.ToString());
        return acting != null && !acting.IsDisabled && await _users.IsInRoleAsync(acting, AccountRoles.Admin) ? null : NotAdminMessage;
    }

    public const string NotAdminMessage = "Alleen voor beheerders.";

    private async Task<bool> IsLastActiveAdminAsync(AppUser user)
    {
        if (!await _users.IsInRoleAsync(user, AccountRoles.Admin)) return false;

        var admins = await _users.GetUsersInRoleAsync(AccountRoles.Admin);
        return admins.Count(a => !a.IsDisabled && a.Id != user.Id) == 0;
    }

    private static AccountInfo ToInfo(AppUser user, AccountKind kind) => new(
        user.Id, user.Email ?? "", user.DisplayName, kind, user.GroupId, user.PasswordHash != null, user.IsDisabled, user.CreatedAt, user.LastLoginAt);
}
