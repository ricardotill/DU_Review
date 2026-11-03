using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScoutingAutomation.Infrastructure.Accounts;
using ScoutingAutomation.Infrastructure.Data;

namespace ScoutingAutomation.Web.Auth;

/// <summary>
/// Adds the display name and, for a group account, its group to the login: the group of the account's team in the
/// active edition. A team that does not play the active edition gets no group, so it sees no group pages.
/// </summary>
public sealed class AccountClaimsFactory : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>
{
    private readonly ScoutingDbContext _db;

    public AccountClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options, ScoutingDbContext db)
        : base(users, roles, options)
    {
        _db = db;
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        // The name pages show; the user name is the email address
        identity.RemoveClaim(identity.FindFirst(identity.NameClaimType));
        identity.AddClaim(new Claim(identity.NameClaimType, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? "" : user.DisplayName));
        if (user.GroupId is { } team && identity.HasClaim(identity.RoleClaimType, AppAuth.GroupRole))
        {
            identity.AddClaim(new Claim(AppAuth.TeamIdClaim, team.ToString()));
            if (await _db.Groups.AsNoTracking().Where(g => g.TeamId == team).Select(g => (Guid?)g.Id).FirstOrDefaultAsync() is { } group)
            {
                identity.AddClaim(new Claim(AppAuth.GroupIdClaim, group.ToString()));
            }
        }

        return identity;
    }
}
