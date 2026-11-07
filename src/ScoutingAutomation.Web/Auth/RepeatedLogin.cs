using Microsoft.AspNetCore.Antiforgery;

namespace ScoutingAutomation.Web.Auth;

public static class RepeatedLogin
{
    /// <summary>
    /// A login form sent again once the browser already has the login cookie (a second submit, a retry on the way,
    /// another tab) carries a token made for an anonymous visitor, so the antiforgery check fails and Blazor answers 400
    /// before the login page runs. That visitor is logged in already: send them to their start page instead.
    /// Nobody is signed in here, so a forged login form still gets nowhere.
    /// </summary>
    public static IApplicationBuilder UseRepeatedLoginRedirect(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method)
                && context.Request.Path.Equals("/login", StringComparison.OrdinalIgnoreCase)
                && context.User.Identity?.IsAuthenticated == true
                && context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
            {
                context.Response.Redirect(AppAuth.StartPage(context.User, context.Request.Query["ReturnUrl"]));
                return Task.CompletedTask;
            }

            return next(context);
        });
}
