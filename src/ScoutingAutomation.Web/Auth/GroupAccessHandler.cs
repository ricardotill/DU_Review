using Microsoft.AspNetCore.Authorization;
using RouteData = Microsoft.AspNetCore.Components.RouteData;

namespace ScoutingAutomation.Web.Auth;

/// <summary>Part of the default policy: the jury may open everything, a group and the beamer only what is marked for them.</summary>
public sealed class StaffOrGroupPageRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets a group login through only on pages and endpoints with <see cref="GroupAccessAttribute"/>, and the beamer only
/// on those with <see cref="BeamerAccessAttribute"/>, so a page added later stays closed to them unless it says otherwise.
/// </summary>
public sealed class GroupAccessHandler : AuthorizationHandler<StaffOrGroupPageRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, StaffOrGroupPageRequirement requirement)
    {
        if (context.User.IsInRole(AppAuth.JuryRole)
            // A group login without its group (claim missing or broken) gets nothing: pages would read "no group" as "all groups"
            || (AppAuth.GroupId(context.User) != null && Allows<GroupAccessAttribute>(context.Resource))
            || (context.User.IsInRole(AppAuth.BeamerRole) && Allows<BeamerAccessAttribute>(context.Resource)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }

    private static bool Allows<TAttribute>(object? resource) where TAttribute : Attribute => resource switch
    {
        // A page opened in the live connection (AuthorizeRouteView)
        RouteData route => route.PageType.IsDefined(typeof(TAttribute), inherit: false),
        // A request: the page or endpoint it goes to, or the live connection itself, whose pages are checked above
        HttpContext http => http.GetEndpoint() is { } endpoint
            && (endpoint.Metadata.GetMetadata<TAttribute>() != null || IsBlazorHub(http.Request.Path)),
        _ => false
    };

    private static bool IsBlazorHub(PathString path) => path.StartsWithSegments("/_blazor");
}
