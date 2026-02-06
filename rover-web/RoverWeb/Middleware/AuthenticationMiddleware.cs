using Microsoft.AspNetCore.Http;

sealed class AuthenticationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expectedPassword;

    public AuthenticationMiddleware(RequestDelegate next, string expectedPassword)
    {
        _next = next;
        _expectedPassword = expectedPassword;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant();

        // Allow access to login page, login API, and static assets needed for login
        if (path == "/login.html" || path == "/api/login" || (path != null && path.StartsWith("/favicon.ico")))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Cookies.TryGetValue("RoverAuth", out var authCookie) || authCookie != _expectedPassword)
        {
            if (path == "/ws")
            {
                context.Response.StatusCode = 401;
                return;
            }

            context.Response.Redirect("/login.html");
            return;
        }

        await _next(context);
    }
}
