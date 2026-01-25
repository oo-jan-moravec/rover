using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Azure.Communication.NetworkTraversal;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

// Add SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB for video frames
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
    options.KeepAliveInterval = TimeSpan.FromSeconds(10);
});

// Add CORS for browser clients
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Add services
builder.Services.AddSingleton<RoverConnectionManager>();
builder.Services.AddSingleton<RelayConfiguration>();
builder.Services.AddSingleton<AzureTurnService>();

var app = builder.Build();

// Initialize configuration
var config = app.Services.GetRequiredService<RelayConfiguration>();
config.RoverApiKey = app.Configuration["RoverRelay:RoverApiKey"] ?? "dev-rover-key";
config.ClientAccessKey = app.Configuration["RoverRelay:ClientAccessKey"] ?? "dev-client-key";

// Parse Azure Communication Services configuration
config.AcsConnectionString = app.Configuration["RoverRelay:AzureCommunicationServices:ConnectionString"] ?? "";
config.RoverWhepUrl = app.Configuration["RoverRelay:RoverWhepUrl"] ?? "";

app.UseCors();

// Serve static files (UI)
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Disable caching for fresh updates
        ctx.Context.Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        ctx.Context.Response.Headers.Append("Pragma", "no-cache");
        ctx.Context.Response.Headers.Append("Expires", "0");
    }
});

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// Status endpoint (shows connected rover/clients)
app.MapGet("/status", (RoverConnectionManager mgr) =>
{
    return Results.Ok(new
    {
        roverConnected = mgr.IsRoverConnected,
        clientCount = mgr.ClientCount,
        timestamp = DateTime.UtcNow
    });
});

// Debug endpoint for TURN credentials - direct test
app.MapGet("/debug/turn", async (RelayConfiguration relayConfig) =>
{
    try
    {
        var hasConnStr = !string.IsNullOrEmpty(relayConfig.AcsConnectionString);
        var connStrPrefix = hasConnStr ? relayConfig.AcsConnectionString.Substring(0, Math.Min(50, relayConfig.AcsConnectionString.Length)) + "..." : "EMPTY";
        
        if (!hasConnStr)
        {
            return Results.Ok(new { success = false, error = "No connection string", acsConfigured = false });
        }
        
        // Direct API call for debugging
        var client = new CommunicationRelayClient(relayConfig.AcsConnectionString);
        var response = await client.GetRelayConfigurationAsync();
        var relayConf = response.Value;
        
        return Results.Ok(new
        {
            success = true,
            connectionStringPrefix = connStrPrefix,
            expiresOn = relayConf.ExpiresOn,
            iceServerCount = relayConf.IceServers?.Count ?? 0,
            iceServers = relayConf.IceServers?.Select(s => new {
                urlCount = s.Urls?.Count() ?? 0,
                urls = s.Urls,
                hasUsername = !string.IsNullOrEmpty(s.Username),
                routeType = s.RouteType.ToString()
            }).ToList()
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, error = ex.Message, exceptionType = ex.GetType().Name, stackTrace = ex.StackTrace?.Split('\n').Take(5) });
    }
});

// Map SignalR hubs
app.MapHub<RoverHub>("/rover");
app.MapHub<ClientHub>("/client");

// WHEP proxy endpoint - forwards WHEP requests to rover via SignalR
app.MapPost("/whep", async (HttpContext ctx, RoverConnectionManager mgr, IHubContext<RoverHub> roverHub, AzureTurnService turnService) =>
{
    if (!mgr.IsRoverConnected || mgr.RoverConnectionId == null)
    {
        ctx.Response.StatusCode = 503;
        await ctx.Response.WriteAsync("Rover not connected");
        return;
    }

    // Read the SDP offer from the request body
    using var reader = new StreamReader(ctx.Request.Body);
    var sdpOffer = await reader.ReadToEndAsync();

    if (string.IsNullOrEmpty(sdpOffer))
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsync("Missing SDP offer");
        return;
    }

    // Get TURN credentials to inject into the request
    var turnCreds = await turnService.GetCredentialsAsync();

    // Create a task completion source to wait for rover response
    var requestId = Guid.NewGuid().ToString();
    var tcs = new TaskCompletionSource<WhepProxyResponse>();

    // Register the pending request
    WhepProxyManager.PendingRequests[requestId] = tcs;

    try
    {
        // Send WHEP request to rover via SignalR
        await roverHub.Clients.Client(mgr.RoverConnectionId).SendAsync("WhepRequest", new
        {
            requestId,
            sdpOffer,
            turnConfig = turnCreds != null ? new
            {
                urls = turnCreds.Urls,
                username = turnCreds.Username,
                credential = turnCreds.Credential
            } : null
        });

        // Wait for response with timeout (20 seconds to allow for slow TURN negotiation)
        var timeoutTask = Task.Delay(20000);
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            ctx.Response.StatusCode = 504;
            await ctx.Response.WriteAsync("Rover timeout");
            return;
        }

        var response = await tcs.Task;

        if (!response.Success)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync(response.Error ?? "WHEP failed");
            return;
        }

        // Return the SDP answer
        ctx.Response.ContentType = "application/sdp";
        ctx.Response.StatusCode = 201;
        
        // Add Location header (WHEP spec)
        var sessionId = Guid.NewGuid().ToString();
        ctx.Response.Headers.Append("Location", $"/whep/{sessionId}");
        
        await ctx.Response.WriteAsync(response.SdpAnswer ?? "");
    }
    finally
    {
        WhepProxyManager.PendingRequests.TryRemove(requestId, out _);
    }
});

// WHEP session endpoint (for ICE candidates, not fully implemented)
app.MapPatch("/whep/{sessionId}", async (HttpContext ctx, string sessionId) =>
{
    // ICE trickle - not critical for initial implementation
    ctx.Response.StatusCode = 204;
});

app.MapDelete("/whep/{sessionId}", (string sessionId) =>
{
    // Session teardown - not critical for initial implementation
    return Results.NoContent();
});

app.Run();

// ===== WHEP Proxy Manager =====

public static class WhepProxyManager
{
    public static readonly ConcurrentDictionary<string, TaskCompletionSource<WhepProxyResponse>> PendingRequests = new();
}

public class WhepProxyResponse
{
    public bool Success { get; set; }
    public string? SdpAnswer { get; set; }
    public string? Error { get; set; }
}

// ===== Configuration =====

public class RelayConfiguration
{
    public string RoverApiKey { get; set; } = "";
    public string ClientAccessKey { get; set; } = "";
    public string AcsConnectionString { get; set; } = "";
    public string RoverWhepUrl { get; set; } = "";
}

// ===== Azure Communication Services TURN Service =====

public class AzureTurnService
{
    private readonly RelayConfiguration _config;
    private readonly ILogger<AzureTurnService> _logger;

    // Cache credentials for 5 minutes (they're valid for 48 hours)
    private TurnCredentials? _cachedCredentials;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AzureTurnService(RelayConfiguration config, ILogger<AzureTurnService> logger)
    {
        _config = config;
        _logger = logger;
    }

    public async Task<TurnCredentials?> GetCredentialsAsync()
    {
        if (string.IsNullOrEmpty(_config.AcsConnectionString))
        {
            _logger.LogWarning("Azure Communication Services not configured");
            return null;
        }

        // Check cache
        if (_cachedCredentials != null && DateTime.UtcNow < _cacheExpiry)
        {
            return _cachedCredentials;
        }

        await _lock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (_cachedCredentials != null && DateTime.UtcNow < _cacheExpiry)
            {
                return _cachedCredentials;
            }

            // Create the Network Traversal client
            var client = new CommunicationRelayClient(_config.AcsConnectionString);

            // Get relay configuration (TURN credentials)
            var response = await client.GetRelayConfigurationAsync();
            var relayConfig = response.Value;

            if (relayConfig.IceServers == null || relayConfig.IceServers.Count == 0)
            {
                _logger.LogError("No ICE servers returned from Azure Communication Services");
                return null;
            }

            // Combine all URLs from all ICE servers
            var allUrls = new List<string>();
            string username = "";
            string credential = "";

            foreach (var server in relayConfig.IceServers)
            {
                allUrls.AddRange(server.Urls);
                username = server.Username;
                credential = server.Credential;
            }

            _cachedCredentials = new TurnCredentials
            {
                Urls = allUrls.ToArray(),
                Username = username,
                Credential = credential
            };
            
            // ACS credentials are valid for 48 hours, cache for 1 hour
            _cacheExpiry = DateTime.UtcNow.AddHours(1);

            _logger.LogInformation("Fetched Azure TURN credentials, {Count} URLs", _cachedCredentials.Urls.Length);
            return _cachedCredentials;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching Azure TURN credentials");
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}

public class TurnCredentials
{
    public string[] Urls { get; set; } = [];
    public string Username { get; set; } = "";
    public string Credential { get; set; } = "";
}

// ===== Connection Manager =====

public class RoverConnectionManager
{
    private readonly ILogger<RoverConnectionManager> _logger;
    private string? _roverConnectionId;
    private readonly ConcurrentDictionary<string, ClientInfo> _clients = new();
    private readonly object _roverLock = new();

    // Current operator
    private string? _operatorConnectionId;
    private readonly ConcurrentDictionary<string, string> _clientNames = new();
    private int _nextPilotNumber = 1;

    public RoverConnectionManager(ILogger<RoverConnectionManager> logger)
    {
        _logger = logger;
    }

    public bool IsRoverConnected => _roverConnectionId != null;
    public string? RoverConnectionId => _roverConnectionId;
    public int ClientCount => _clients.Count;

    public bool RegisterRover(string connectionId)
    {
        lock (_roverLock)
        {
            if (_roverConnectionId != null)
            {
                _logger.LogWarning("Rover already connected, rejecting new connection {ConnectionId}", connectionId);
                return false;
            }
            _roverConnectionId = connectionId;
            _logger.LogInformation("Rover registered: {ConnectionId}", connectionId);
            return true;
        }
    }

    public void UnregisterRover(string connectionId)
    {
        lock (_roverLock)
        {
            if (_roverConnectionId == connectionId)
            {
                _roverConnectionId = null;
                _logger.LogInformation("Rover unregistered: {ConnectionId}", connectionId);
            }
        }
    }

    public string RegisterClient(string connectionId)
    {
        var name = $"Pilot-{_nextPilotNumber++}";
        _clients.TryAdd(connectionId, new ClientInfo { Name = name, ConnectedAt = DateTime.UtcNow });
        _clientNames.TryAdd(connectionId, name);
        _logger.LogInformation("Client registered: {ConnectionId} as {Name}", connectionId, name);
        return name;
    }

    public void UnregisterClient(string connectionId)
    {
        _clients.TryRemove(connectionId, out _);
        _clientNames.TryRemove(connectionId, out _);

        // If operator disconnected, clear operator
        if (_operatorConnectionId == connectionId)
        {
            _operatorConnectionId = null;
            _logger.LogInformation("Operator {ConnectionId} disconnected, operator cleared", connectionId);
        }

        _logger.LogInformation("Client unregistered: {ConnectionId}", connectionId);
    }

    public IEnumerable<string> GetAllClientIds() => _clients.Keys;

    public string? GetClientName(string connectionId)
    {
        return _clientNames.TryGetValue(connectionId, out var name) ? name : null;
    }

    // Operator management
    public bool IsOperator(string connectionId) => _operatorConnectionId == connectionId;
    public string? GetOperatorConnectionId() => _operatorConnectionId;
    public string? GetOperatorName()
    {
        if (_operatorConnectionId != null && _clientNames.TryGetValue(_operatorConnectionId, out var name))
            return name;
        return null;
    }

    public bool TryClaim(string connectionId)
    {
        if (_operatorConnectionId == null)
        {
            _operatorConnectionId = connectionId;
            _logger.LogInformation("Client {ConnectionId} claimed operator", connectionId);
            return true;
        }
        return false;
    }

    public bool ReleaseOperator(string connectionId)
    {
        if (_operatorConnectionId == connectionId)
        {
            _operatorConnectionId = null;
            _logger.LogInformation("Client {ConnectionId} released operator", connectionId);
            return true;
        }
        return false;
    }
}

public class ClientInfo
{
    public string Name { get; set; } = "";
    public DateTime ConnectedAt { get; set; }
}

// ===== Rover Hub =====

public class RoverHub : Hub
{
    private readonly RoverConnectionManager _manager;
    private readonly RelayConfiguration _config;
    private readonly IHubContext<ClientHub> _clientHub;
    private readonly ILogger<RoverHub> _logger;

    public RoverHub(
        RoverConnectionManager manager,
        RelayConfiguration config,
        IHubContext<ClientHub> clientHub,
        ILogger<RoverHub> logger)
    {
        _manager = manager;
        _config = config;
        _clientHub = clientHub;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Check API key from query string
        var apiKey = Context.GetHttpContext()?.Request.Query["apiKey"].FirstOrDefault();
        if (apiKey != _config.RoverApiKey)
        {
            _logger.LogWarning("Rover connection rejected: invalid API key from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        if (!_manager.RegisterRover(Context.ConnectionId))
        {
            _logger.LogWarning("Rover connection rejected: rover already connected");
            Context.Abort();
            return;
        }

        _logger.LogInformation("Rover connected: {ConnectionId}", Context.ConnectionId);

        // Notify all clients that rover is online
        await _clientHub.Clients.All.SendAsync("RoverStatus", new { online = true });

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _manager.UnregisterRover(Context.ConnectionId);
        _logger.LogInformation("Rover disconnected: {ConnectionId}", Context.ConnectionId);

        // Notify all clients that rover is offline
        await _clientHub.Clients.All.SendAsync("RoverStatus", new { online = false });

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Rover sends telemetry, broadcast to all clients
    /// </summary>
    public async Task SendTelemetry(object telemetry)
    {
        await _clientHub.Clients.All.SendAsync("Telemetry", telemetry);
    }

    /// <summary>
    /// Rover sends role update, broadcast to all clients
    /// </summary>
    public async Task SendRoleUpdate(string role, string? extra)
    {
        await _clientHub.Clients.All.SendAsync("RoleUpdate", new { role, extra });
    }

    /// <summary>
    /// Rover responds to a specific client (e.g., rescan result)
    /// </summary>
    public async Task SendToClient(string clientConnectionId, string messageType, object data)
    {
        await _clientHub.Clients.Client(clientConnectionId).SendAsync(messageType, data);
    }

    /// <summary>
    /// Rover sends WebRTC signaling (answer/ICE candidates) to specific client
    /// </summary>
    public async Task SendWebRtcSignal(string clientConnectionId, object signal)
    {
        await _clientHub.Clients.Client(clientConnectionId).SendAsync("WebRtcSignal", signal);
    }

    /// <summary>
    /// Rover responds to a WHEP proxy request
    /// </summary>
    public Task WhepResponse(string requestId, bool success, string? sdpAnswer, string? error)
    {
        if (WhepProxyManager.PendingRequests.TryGetValue(requestId, out var tcs))
        {
            tcs.TrySetResult(new WhepProxyResponse
            {
                Success = success,
                SdpAnswer = sdpAnswer,
                Error = error
            });
        }
        return Task.CompletedTask;
    }
}

// ===== Client Hub =====

public class ClientHub : Hub
{
    private readonly RoverConnectionManager _manager;
    private readonly RelayConfiguration _config;
    private readonly AzureTurnService _turnService;
    private readonly IHubContext<RoverHub> _roverHub;
    private readonly ILogger<ClientHub> _logger;

    public ClientHub(
        RoverConnectionManager manager,
        RelayConfiguration config,
        AzureTurnService turnService,
        IHubContext<RoverHub> roverHub,
        ILogger<ClientHub> logger)
    {
        _manager = manager;
        _config = config;
        _turnService = turnService;
        _roverHub = roverHub;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Check access key from query string
        var accessKey = Context.GetHttpContext()?.Request.Query["accessKey"].FirstOrDefault();
        if (accessKey != _config.ClientAccessKey)
        {
            _logger.LogWarning("Client connection rejected: invalid access key from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var clientName = _manager.RegisterClient(Context.ConnectionId);
        _logger.LogInformation("Client connected: {ConnectionId} as {Name}", Context.ConnectionId, clientName);

        // Get TURN credentials from Cloudflare
        var turnCreds = await _turnService.GetCredentialsAsync();

        // Send client their name and initial status
        await Clients.Caller.SendAsync("Welcome", new
        {
            name = clientName,
            roverOnline = _manager.IsRoverConnected,
            roverWhepUrl = _config.RoverWhepUrl,
            turnConfig = turnCreds != null ? new
            {
                urls = turnCreds.Urls,
                username = turnCreds.Username,
                credential = turnCreds.Credential
            } : null
        });

        // Send role status
        await SendRoleStatusToCaller();

        // Notify rover of new client
        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("ClientJoined", new { connectionId = Context.ConnectionId, name = clientName });
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var wasOperator = _manager.IsOperator(Context.ConnectionId);
        _manager.UnregisterClient(Context.ConnectionId);

        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);

        // Notify rover of client leaving
        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("ClientLeft", new { connectionId = Context.ConnectionId });
        }

        // If operator left, broadcast role update
        if (wasOperator)
        {
            await BroadcastRoleUpdates();

            // Tell rover to stop
            if (_manager.RoverConnectionId != null)
            {
                await _roverHub.Clients.Client(_manager.RoverConnectionId)
                    .SendAsync("OperatorDisconnected");
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Client sends motor command
    /// </summary>
    public async Task SendMotorCommand(int left, int right)
    {
        if (!_manager.IsOperator(Context.ConnectionId))
        {
            _logger.LogDebug("Non-operator tried to send motor command");
            return;
        }

        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("MotorCommand", new { left, right });
        }
    }

    /// <summary>
    /// Client sends stop command
    /// </summary>
    public async Task SendStop()
    {
        if (!_manager.IsOperator(Context.ConnectionId)) return;

        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("Stop");
        }
    }

    /// <summary>
    /// Client sends headlight command
    /// </summary>
    public async Task SendHeadlight(bool on)
    {
        if (!_manager.IsOperator(Context.ConnectionId)) return;

        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("Headlight", new { on });
        }
    }

    /// <summary>
    /// Client sends rescan command
    /// </summary>
    public async Task SendRescan()
    {
        if (!_manager.IsOperator(Context.ConnectionId)) return;

        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("Rescan", new { clientConnectionId = Context.ConnectionId });
        }
    }

    /// <summary>
    /// Client claims operator role (when no operator)
    /// </summary>
    public async Task Claim()
    {
        if (_manager.TryClaim(Context.ConnectionId))
        {
            await BroadcastRoleUpdates();

            // Notify rover
            if (_manager.RoverConnectionId != null)
            {
                await _roverHub.Clients.Client(_manager.RoverConnectionId)
                    .SendAsync("OperatorChanged", new { name = _manager.GetClientName(Context.ConnectionId) });
            }
        }
        else
        {
            await SendRoleStatusToCaller();
        }
    }

    /// <summary>
    /// Client releases operator role
    /// </summary>
    public async Task Release()
    {
        if (_manager.ReleaseOperator(Context.ConnectionId))
        {
            await BroadcastRoleUpdates();

            // Notify rover to stop and clear operator
            if (_manager.RoverConnectionId != null)
            {
                await _roverHub.Clients.Client(_manager.RoverConnectionId)
                    .SendAsync("OperatorReleased");
            }
        }
    }

    /// <summary>
    /// Client sends WebRTC signaling (offer/ICE candidates) to rover
    /// </summary>
    public async Task SendWebRtcSignal(object signal)
    {
        if (_manager.RoverConnectionId != null)
        {
            await _roverHub.Clients.Client(_manager.RoverConnectionId)
                .SendAsync("WebRtcSignal", new { clientConnectionId = Context.ConnectionId, signal });
        }
    }

    private async Task SendRoleStatusToCaller()
    {
        var isOp = _manager.IsOperator(Context.ConnectionId);
        var opName = _manager.GetOperatorName();

        await Clients.Caller.SendAsync("RoleStatus", new
        {
            isOperator = isOp,
            operatorName = opName
        });
    }

    private async Task BroadcastRoleUpdates()
    {
        var opName = _manager.GetOperatorName();
        var opId = _manager.GetOperatorConnectionId();

        foreach (var clientId in _manager.GetAllClientIds())
        {
            var isOp = clientId == opId;
            await Clients.Client(clientId).SendAsync("RoleStatus", new
            {
                isOperator = isOp,
                operatorName = opName
            });
        }
    }
}
