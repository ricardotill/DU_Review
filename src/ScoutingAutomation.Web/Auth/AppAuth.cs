using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using ScoutingAutomation.Infrastructure.Accounts;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Logins are accounts (ASP.NET Identity, see Program.cs). This holds the roles and claims pages check.
/// </summary>
public static class AppAuth
{
    public const string AdminRole = AccountRoles.Admin;

    /// <summary>Jury and organisers: everyone with a jury account, admins included.</summary>
    public const string JuryRole = AccountRoles.Jury;

    /// <summary>A contestant group: only pages marked <see cref="GroupAccessAttribute"/>, and only its own data.</summary>
    public const string GroupRole = AccountRoles.Group;

    /// <summary>The beamer screen: only pages marked <see cref="BeamerAccessAttribute"/>.</summary>
    public const string BeamerRole = AccountRoles.Beamer;

    public static readonly string Scheme = IdentityConstants.ApplicationScheme;

    /// <summary>The group of a group login.</summary>
    public const string GroupIdClaim = "du:group";

    /// <summary>The team of a group login: the group across editions.</summary>
    public const string TeamIdClaim = "du:team";

    /// <summary>Whether this is a group login rather than jury, beamer or admin.</summary>
    public static bool IsGroup(ClaimsPrincipal user) => user.IsInRole(GroupRole);

    /// <summary>Whether this is the beamer screen.</summary>
    public static bool IsBeamer(ClaimsPrincipal user) => user.IsInRole(BeamerRole) && !user.IsInRole(JuryRole);

    /// <summary>The group of a group login; null for jury, beamer and admins.</summary>
    public static Guid? GroupId(ClaimsPrincipal user) =>
        IsGroup(user) && Guid.TryParse(user.FindFirstValue(GroupIdClaim), out var id) ? id : null;

    /// <summary>The team of a group login, also when it does not play the active edition; null for everyone else.</summary>
    public static Guid? TeamId(ClaimsPrincipal user) =>
        IsGroup(user) && Guid.TryParse(user.FindFirstValue(TeamIdClaim), out var id) ? id : null;

    /// <summary>The account of a login.</summary>
    public static Guid? UserId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Where a login starts: a group and the beamer have no use for a jury page.</summary>
    public static string StartPage(ClaimsPrincipal user, string? returnUrl) =>
        IsBeamer(user) ? "/scoreboard" : IsGroup(user) ? (GroupId(user) == null ? "/edities" : "/") : SafeReturnUrl(returnUrl);

    /// <summary>Only local paths, so a login link cannot send people to another site.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}
