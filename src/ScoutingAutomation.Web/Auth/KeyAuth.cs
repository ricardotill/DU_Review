using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ScoutingAutomation.Infrastructure.Access;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Logins made with the shared access key (jury, optionally with the admin key) or with a group key.
/// A login remembers the stamp of the key it was made with; once a key is regenerated, the stamp no longer matches.
/// </summary>
public static class KeyAuth
{
    public const string AdminRole = "Admin";

    /// <summary>Jury and organisers: everyone logged in with the access key, admins included.</summary>
    public const string JuryRole = "Jury";

    /// <summary>A contestant group: only pages marked <see cref="GroupAccessAttribute"/>, and only its own data.</summary>
    public const string GroupRole = "Group";

    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    private const string AccessStampClaim = "du:access";
    private const string AdminStampClaim = "du:admin";
    /// <summary>The group of a group login.</summary>
    public const string GroupIdClaim = "du:group";
    private const string GroupChannelClaim = "du:groupchannel";
    private const string GroupStampClaim = "du:groupstamp";

    public static ClaimsPrincipal CreatePrincipal(AccessKeyService keys, bool admin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin ? "Admin" : "Jury"),
            new(AccessStampClaim, keys.AccessStamp),
            new(ClaimTypes.Role, JuryRole)
        };

        if (admin)
        {
            claims.Add(new Claim(AdminStampClaim, keys.AdminStamp));
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    public static ClaimsPrincipal CreateGroupPrincipal(AccessKeyService keys, Guid groupId, string channelId, string groupName) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, groupName),
            new Claim(ClaimTypes.Role, GroupRole),
            new Claim(GroupIdClaim, groupId.ToString()),
            new Claim(GroupChannelClaim, channelId),
            new Claim(GroupStampClaim, keys.GroupStamp(channelId) ?? "")
        ], Scheme));

    /// <summary>Whether this is a group login rather than jury or admin.</summary>
    public static bool IsGroup(ClaimsPrincipal user) => user.IsInRole(GroupRole);

    /// <summary>The group of a group login; null for jury and admins.</summary>
    public static Guid? GroupId(ClaimsPrincipal user) =>
        IsGroup(user) && Guid.TryParse(user.FindFirstValue(GroupIdClaim), out var id) ? id : null;

    /// <summary>
    /// Checks a login against the current keys.
    /// </summary>
    /// <returns>
    /// The same principal when still valid; one without admin rights when the admin key changed;
    /// null when the access key (or for a group: its group key) changed and the login is no longer valid.
    /// </returns>
    public static ClaimsPrincipal? Revalidate(ClaimsPrincipal user, AccessKeyService keys)
    {
        if (user.Identity?.IsAuthenticated != true) return null;

        if (IsGroup(user))
        {
            var channel = user.FindFirstValue(GroupChannelClaim);
            var stamp = channel == null ? null : keys.GroupStamp(channel);
            return stamp != null && stamp == user.FindFirstValue(GroupStampClaim) && GroupId(user) != null ? user : null;
        }

        if (user.FindFirstValue(AccessStampClaim) != keys.AccessStamp)
        {
            return null;
        }

        var adminStamp = user.FindFirstValue(AdminStampClaim);
        if (adminStamp != null && adminStamp != keys.AdminStamp) return CreatePrincipal(keys, admin: false);

        // A login from before the jury role existed: same rights, now with the role
        return user.IsInRole(JuryRole) ? user : CreatePrincipal(keys, admin: adminStamp != null);
    }

    /// <summary>
    /// Signs this browser in with the current keys. The request itself continues as the new login too:
    /// otherwise a key regenerated during this request would log out the person who regenerated it.
    /// </summary>
    public static Task SignInAsync(HttpContext http, AccessKeyService keys, bool admin) =>
        SignInAsync(http, CreatePrincipal(keys, admin));

    /// <inheritdoc cref="SignInAsync(HttpContext, AccessKeyService, bool)"/>
    public static async Task SignInAsync(HttpContext http, ClaimsPrincipal principal)
    {
        await http.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = true });
        http.User = principal;

        var authState = http.RequestServices.GetService<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider>();
        if (authState is Microsoft.AspNetCore.Components.Authorization.IHostEnvironmentAuthenticationStateProvider host)
        {
            host.SetAuthenticationState(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(principal)));
        }
    }

    /// <summary>Runs on every request with a login cookie.</summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var keys = context.HttpContext.RequestServices.GetRequiredService<AccessKeyService>();
        var validated = context.Principal == null ? null : Revalidate(context.Principal, keys);
        if (validated == null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(Scheme);
        }
        else if (!ReferenceEquals(validated, context.Principal))
        {
            context.ReplacePrincipal(validated);
            context.ShouldRenew = true;
        }
    }

    /// <summary>Only local paths, so a login link cannot send people to another site.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}
