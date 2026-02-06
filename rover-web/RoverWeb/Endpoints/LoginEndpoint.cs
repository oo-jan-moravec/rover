using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

static class LoginEndpoint
{
    public static void MapLoginEndpoint(this IEndpointRouteBuilder app, string roverPassword)
    {
        app.MapPost("/api/login", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

            if (data != null && data.TryGetValue("password", out var password) && password == roverPassword)
            {
                context.Response.Cookies.Append("RoverAuth", roverPassword, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = false, // Set to true if using HTTPS
                    SameSite = SameSiteMode.Strict,
                    Expires = DateTimeOffset.UtcNow.AddDays(7)
                });
                return Results.Ok();
            }

            return Results.Unauthorized();
        });
    }
}
