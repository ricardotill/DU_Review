using Microsoft.AspNetCore.Authorization;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace ScoutingAutomation.Web.Auth;

/// <summary>Part of the default policy: the jury may open everything, a group only what is marked for it.</summary>
public sealed class StaffOrGroupPageRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets a group login through only on pages and endpoints with <see cref="GroupAccessAttribute"/>, so a page added
/// later stays closed to groups unless it says otherwise.
/// </summary>
public sealed class GroupAccessHandler : AuthorizationHandler<StaffOrGroupPageRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StaffOrGroupPageRequirement requirement)
    {
        if (context.User.IsInRole(KeyAuth.JuryRole) || (KeyAuth.IsGroup(context.User) && AllowsGroups(context.Resource)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool AllowsGroups(object? resource) => resource switch
    {
        // A page opened in the live connection (AuthorizeRouteView)
        RouteData route => route.PageType.IsDefined(typeof(GroupAccessAttribute), inherit: false),
        // A request: the page or endpoint it goes to, or the live connection itself, whose pages are checked above
        HttpContext http => http.GetEndpoint() is { } endpoint
            && (endpoint.Metadata.GetMetadata<GroupAccessAttribute>() != null || IsBlazorHub(http.Request.Path)),
        _ => false
    };

    private static bool IsBlazorHub(PathString path) => path.StartsWithSegments("/_blazor");
}
