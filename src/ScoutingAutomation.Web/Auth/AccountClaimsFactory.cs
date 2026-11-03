using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Infrastructure.Accounts;

namespace ScoutingAutomation.Web.Auth;

/// <summary>Adds the display name and, for a group account, its group to the login.</summary>
public sealed class AccountClaimsFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>
{
    public AccountClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options)
        : base(users, roles, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        // The name pages show; the user name is the email address
        identity.RemoveClaim(identity.FindFirst(identity.NameClaimType));
        identity.AddClaim(new Claim(identity.NameClaimType, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName));
        if (user.GroupId is { } group && identity.HasClaim(identity.RoleClaimType, AppAuth.GroupRole))
        {
            identity.AddClaim(new Claim(AppAuth.GroupIdClaim, group.ToString()));
        }

        return identity;
    }
}
