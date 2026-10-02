namespace ScoutingAutomation.Web.Security;

public static class SecurityHeaders
{
    /// <summary>
    /// Headers that keep other sites from showing the app in a frame (so no one can trick an admin into clicking
    /// "regenerate key"), keep browsers from guessing file types, and keep page addresses out of other sites' logs.
    /// The policy leaves scripts alone: Blazor's import map is an inline script.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            headers.ContentSecurityPolicy = "frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
            return next(context);
        });
}
