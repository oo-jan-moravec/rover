using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RoverState>();
builder.Services.AddSingleton<WebSocketManager>();
builder.Services.AddSingleton<RoverLogService>();
builder.Services.AddSingleton<OperatorManager>();
builder.Services.AddSingleton<GpioController>();
builder.Services.AddSingleton<WifiState>();
builder.Services.AddSingleton<SafetyStateMachine>();
builder.Services.AddSingleton<WifiMonitor>();
builder.Services.AddSingleton<AudioCaptureService>();
builder.Services.AddSingleton<AudioPlaybackService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WifiMonitor>());
builder.Services.AddHostedService<SerialPump>();
builder.Services.AddHostedService<DiagnosticsPump>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AudioCaptureService>());
builder.Services.AddHttpClient();

var app = builder.Build();

var roverPassword = app.Configuration["RoverPassword"];
if (string.IsNullOrWhiteSpace(roverPassword))
{
    throw new InvalidOperationException(
        "RoverPassword must be set. Use environment variable RoverPassword or add to User Secrets.");
}

app.UseMiddleware<AuthenticationMiddleware>(roverPassword);

LoginEndpoint.MapLoginEndpoint(app, roverPassword);

app.UseDefaultFiles();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
        ctx.Context.Response.Headers.Append("Expires", "0");
    }
});

app.UseWebSockets();

WebSocketEndpoint.MapWebSocketEndpoint(app);
VideoProxyEndpoint.MapVideoProxyEndpoint(app);
WifiMetricsEndpoint.MapWifiMetricsEndpoint(app);

app.Run("http://0.0.0.0:8080");
