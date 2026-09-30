using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ScoutingAutomation.Infrastructure.Services;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Logins made with the shared access key (and optionally the admin key). A login remembers the stamp
/// of the key it was made with; once a key is regenerated, the stamp no longer matches.
/// </summary>
public static class KeyAuth
{
    public const string AdminRole = "Admin";
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;

    private const string AccessStampClaim = "du:access";
    private const string AdminStampClaim = "du:admin";

    public static ClaimsPrincipal CreatePrincipal(AccessKeyService keys, bool admin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, admin ? "Admin" : "Jury"),
            new(AccessStampClaim, keys.AccessStamp)
        };

        if (admin)
        {
            claims.Add(new Claim(AdminStampClaim, keys.AdminStamp));
            claims.Add(new Claim(ClaimTypes.Role, AdminRole));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>
    /// Checks a login against the current keys.
    /// </summary>
    /// <returns>
    /// The same principal when still valid; one without admin rights when the admin key changed;
    /// null when the access key changed and the login is no longer valid.
    /// </returns>
    public static ClaimsPrincipal? Revalidate(ClaimsPrincipal user, AccessKeyService keys)
    {
        if (user.Identity?.IsAuthenticated != true || user.FindFirstValue(AccessStampClaim) != keys.AccessStamp)
        {
            return null;
        }

        var adminStamp = user.FindFirstValue(AdminStampClaim);
        return adminStamp != null && adminStamp != keys.AdminStamp ? CreatePrincipal(keys, admin: false) : user;
    }

    /// <summary>
    /// Signs this browser in with the current keys. The request itself continues as the new login too:
    /// otherwise a key regenerated during this request would log out the person who regenerated it.
    /// </summary>
    public static async Task SignInAsync(HttpContext http, AccessKeyService keys, bool admin)
    {
        var principal = CreatePrincipal(keys, admin);
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
