using System.Collections.Concurrent;
using System.Device.Gpio;
using System.Diagnostics;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

const string CLIENT_VERSION = "1.2.0"; // Added nearby APs display

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoverState>();
builder.Services.AddSingleton<WebSocketManager>();
builder.Services.AddSingleton<OperatorManager>();
builder.Services.AddSingleton<GpioController>();
builder.Services.AddSingleton<WifiState>();
builder.Services.AddSingleton<SafetyStateMachine>();
builder.Services.AddSingleton<WifiMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<WifiMonitor>());
builder.Services.AddHostedService<SerialPump>();
builder.Services.AddHostedService<DiagnosticsPump>();

var app = builder.Build();

app.UseDefaultFiles();

// Disable caching for all static files to ensure clients always get fresh code
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

app.Map("/ws", async (HttpContext ctx, RoverState state, WebSocketManager wsManager, OperatorManager opManager, GpioController gpio, SafetyStateMachine safetyMachine) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var clientId = Guid.NewGuid();
    var clientName = opManager.RegisterClient(clientId);
    wsManager.AddClient(clientId, ws);

    // Helper to send role status to a specific client
    async Task SendRoleStatus(Guid targetId)
    {
        if (opManager.IsOperator(targetId))
        {
            var pendingName = opManager.GetPendingRequesterName();
            if (pendingName != null)
                await wsManager.SendToClientAsync(targetId, $"ROLE:operator|{pendingName}");
            else
                await wsManager.SendToClientAsync(targetId, "ROLE:operator");
        }
        else
        {
            var operatorName = opManager.GetCurrentOperatorName();
            if (operatorName != null)
                await wsManager.SendToClientAsync(targetId, $"ROLE:spectator|{operatorName}");
            else
                await wsManager.SendToClientAsync(targetId, "ROLE:spectator|none");
        }
    }

    // Helper to broadcast role updates to all clients
    async Task BroadcastRoleUpdates()
    {
        foreach (var id in wsManager.GetAllClientIds())
        {
            await SendRoleStatus(id);
        }
    }

    try
    {
        var buffer = new byte[256];

        // Wait for version handshake first
        var versionResult = await ws.ReceiveAsync(buffer, CancellationToken.None);
        if (versionResult.MessageType == WebSocketMessageType.Close) return;

        var versionMsg = Encoding.UTF8.GetString(buffer, 0, versionResult.Count).Trim();

        if (!versionMsg.StartsWith("VERSION:"))
        {
            // No version provided - outdated client
            await wsManager.SendToClientAsync(clientId, $"VERSION_MISMATCH:{CLIENT_VERSION}");
            return;
        }

        var clientVersion = versionMsg.Substring(8);
        if (clientVersion != CLIENT_VERSION)
        {
            // Version mismatch - client needs to reload
            await wsManager.SendToClientAsync(clientId, $"VERSION_MISMATCH:{CLIENT_VERSION}");
            return;
        }

        // Version OK - continue with normal setup
        await wsManager.SendToClientAsync(clientId, "VERSION_OK");
        await wsManager.SendToClientAsync(clientId, $"NAME:{clientName}");
        await SendRoleStatus(clientId);

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();

            // Handle operator/spectator protocol
            if (msg == "CLAIM")
            {
                if (opManager.TryClaim(clientId))
                {
                    await BroadcastRoleUpdates();
                }
                else
                {
                    await SendRoleStatus(clientId);
                }
            }
            else if (msg == "REQUEST")
            {
                if (opManager.RequestControl(clientId, out var operatorId))
                {
                    // Auto-granted (no operator was present)
                    await BroadcastRoleUpdates();
                }
                else if (operatorId.HasValue)
                {
                    // Notify operator of request
                    await BroadcastRoleUpdates();
                }
            }
            else if (msg == "ACCEPT")
            {
                var (success, newOperatorId, oldOperatorId) = opManager.AcceptRequest(clientId);
                if (success && newOperatorId.HasValue)
                {
                    await wsManager.SendToClientAsync(newOperatorId.Value, "GRANTED");
                    await BroadcastRoleUpdates();
                    // Stop the rover when control transfers
                    state.Set(0, 0);
                }
            }
            else if (msg == "DENY")
            {
                var (success, requesterId) = opManager.DenyRequest(clientId);
                if (success && requesterId.HasValue)
                {
                    await wsManager.SendToClientAsync(requesterId.Value, "DENIED");
                    await BroadcastRoleUpdates();
                }
            }
            else if (msg == "RELEASE")
            {
                if (opManager.ReleaseControl(clientId))
                {
                    state.Set(0, 0); // Stop rover
                    await BroadcastRoleUpdates();
                }
            }
            // Handle control commands - only from operator
            else if (opManager.IsOperator(clientId))
            {
                state.Touch();

                if (msg == "S")
                {
                    state.Set(0, 0);
                }
                else if (msg.StartsWith("M "))
                {
                    // Block motor commands if motors are inhibited (ROAMING or OFFLINE)
                    if (safetyMachine.AreMotorsInhibited())
                    {
                        // Silently ignore motor commands during safety inhibit
                        // The state machine already set motors to 0
                        continue;
                    }

                    var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 3 &&
                        int.TryParse(parts[1], out var l) &&
                        int.TryParse(parts[2], out var r))
                    {
                        state.Set(Clamp(l), Clamp(r));
                    }
                }
                else if (msg.StartsWith("H "))
                {
                    var parts = msg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && int.TryParse(parts[1], out var state_val))
                    {
                        gpio.SetHeadlight(state_val == 1);
                    }
                }
                else if (msg == "RESCAN")
                {
                    // Trigger Wi-Fi rescan and connect to best AP
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await wsManager.SendToClientAsync(clientId, "RESCAN:started");
                            var result = await TriggerRescanAndRoamAsync();
                            await wsManager.SendToClientAsync(clientId, $"RESCAN:{result}");
                        }
                        catch (Exception ex)
                        {
                            await wsManager.SendToClientAsync(clientId, $"RESCAN:error:{ex.Message}");
                        }
                    });
                }
            }
        }
    }
    finally
    {
        var wasOperator = opManager.IsOperator(clientId);
        opManager.UnregisterClient(clientId);
        wsManager.RemoveClient(clientId);

        // If operator disconnected, notify all clients
        if (wasOperator)
        {
            state.Set(0, 0); // Stop rover
            foreach (var id in wsManager.GetAllClientIds())
            {
                await SendRoleStatus(id);
            }
        }

        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
        catch { /* ignore */ }
    }
});

app.Run("http://0.0.0.0:8080");

static int Clamp(int v) => Math.Max(-255, Math.Min(255, v));

/// <summary>
/// Trigger a Wi-Fi interface restart, rescan, and roam to the best available AP
/// </summary>
static async Task<string> TriggerRescanAndRoamAsync()
{
    try
    {
        // Step 1: Restart the Wi-Fi interface to force a fresh scan
        // This clears any driver state issues and finds all APs
        var downPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/ifconfig",
            Arguments = "wlan0 down",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var downProcess = Process.Start(downPsi))
        {
            if (downProcess != null)
                await downProcess.WaitForExitAsync();
        }

        await Task.Delay(1500); // Wait for interface to go down

        var upPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/ifconfig",
            Arguments = "wlan0 up",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var upProcess = Process.Start(upPsi))
        {
            if (upProcess != null)
                await upProcess.WaitForExitAsync();
        }

        await Task.Delay(3000); // Wait for interface to reconnect

        // Step 2: Trigger scan
        var scanPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/wpa_cli",
            Arguments = "-i wlan0 scan",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var scanProcess = Process.Start(scanPsi))
        {
            if (scanProcess != null)
                await scanProcess.WaitForExitAsync();
        }

        await Task.Delay(2000); // Wait for scan to complete

        // Get current connection info
        var linkPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/iw",
            Arguments = "dev wlan0 link",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string currentBssid = "";
        string currentSsid = "";
        int currentRssi = -100;

        using (var linkProcess = Process.Start(linkPsi))
        {
            if (linkProcess != null)
            {
                var linkOutput = await linkProcess.StandardOutput.ReadToEndAsync();
                await linkProcess.WaitForExitAsync();

                var bssidMatch = System.Text.RegularExpressions.Regex.Match(linkOutput, @"Connected to ([0-9a-fA-F:]+)");
                if (bssidMatch.Success)
                    currentBssid = bssidMatch.Groups[1].Value.ToUpper();

                var ssidMatch = System.Text.RegularExpressions.Regex.Match(linkOutput, @"SSID: (.+)");
                if (ssidMatch.Success)
                    currentSsid = ssidMatch.Groups[1].Value.Trim();

                var signalMatch = System.Text.RegularExpressions.Regex.Match(linkOutput, @"signal: (-?\d+)");
                if (signalMatch.Success && int.TryParse(signalMatch.Groups[1].Value, out var rssi))
                    currentRssi = rssi;
            }
        }

        // Get scan results
        var resultsPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/wpa_cli",
            Arguments = "-i wlan0 scan_results",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        string bestBssid = "";
        int bestRssi = -100;

        using (var resultsProcess = Process.Start(resultsPsi))
        {
            if (resultsProcess != null)
            {
                var resultsOutput = await resultsProcess.StandardOutput.ReadToEndAsync();
                await resultsProcess.WaitForExitAsync();

                var lines = resultsOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines.Skip(1))
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 4)
                    {
                        var ssid = parts.Length >= 5 ? parts[4].Trim() : "";

                        // Only consider APs with same SSID or empty SSID (mesh nodes)
                        if (!string.IsNullOrEmpty(currentSsid) &&
                            ssid != currentSsid && !string.IsNullOrEmpty(ssid))
                            continue;

                        if (int.TryParse(parts[2], out var apRssi) && apRssi > bestRssi)
                        {
                            bestRssi = apRssi;
                            bestBssid = parts[0].ToUpper();
                        }
                    }
                }
            }
        }

        // Check if we should roam
        if (string.IsNullOrEmpty(bestBssid))
        {
            return "no_aps_found";
        }

        if (bestBssid.Equals(currentBssid, StringComparison.OrdinalIgnoreCase))
        {
            return $"already_best:{bestRssi}";
        }

        if (bestRssi <= currentRssi + 3) // Need at least 3dB improvement
        {
            return $"no_better_ap:{currentRssi}:{bestRssi}";
        }

        // Roam to best AP
        var roamPsi = new ProcessStartInfo
        {
            FileName = "/usr/sbin/wpa_cli",
            Arguments = $"-i wlan0 roam {bestBssid}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using (var roamProcess = Process.Start(roamPsi))
        {
            if (roamProcess != null)
            {
                var roamOutput = await roamProcess.StandardOutput.ReadToEndAsync();
                await roamProcess.WaitForExitAsync();

                if (roamOutput.Contains("OK"))
                {
                    return $"roamed:{bestBssid}:{bestRssi}";
                }
                else
                {
                    return $"roam_failed:{roamOutput.Trim()}";
                }
            }
        }

        return "unknown_error";
    }
    catch (Exception ex)
    {
        return $"error:{ex.Message}";
    }
}

sealed class WebSocketManager
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly ILogger<WebSocketManager>? _logger;

    public WebSocketManager(ILogger<WebSocketManager>? logger = null)
    {
        _logger = logger;
    }

    public void AddClient(Guid id, WebSocket ws)
    {
        _clients.TryAdd(id, ws);
        _logger?.LogInformation($"WebSocket client added: {id}. Total clients: {_clients.Count}");
    }

    public void RemoveClient(Guid id)
    {
        if (_clients.TryRemove(id, out _))
        {
            _logger?.LogInformation($"WebSocket client removed: {id}. Total clients: {_clients.Count}");
        }
    }

    public async Task SendToClientAsync(Guid clientId, string message)
    {
        if (_clients.TryGetValue(clientId, out var ws) && ws.State == WebSocketState.Open)
        {
            try
            {
                var buffer = Encoding.UTF8.GetBytes(message);
                await ws.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch
            {
                // Client disconnected
            }
        }
    }

    public Task BroadcastAsync(string message)
    {
        if (_clients.IsEmpty) return Task.CompletedTask;

        var buffer = Encoding.UTF8.GetBytes(message);
        var deadClients = new List<Guid>();

        // Send to all clients, but don't wait for all to complete
        foreach (var (id, ws) in _clients)
        {
            if (ws.State != WebSocketState.Open)
            {
                deadClients.Add(id);
                continue;
            }

            try
            {
                // Fire and forget - don't block on slow clients
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ws.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch
                    {
                        // Client disconnected, will be cleaned up
                    }
                });
            }
            catch
            {
                deadClients.Add(id);
            }
        }

        // Clean up dead clients
        foreach (var id in deadClients)
        {
            RemoveClient(id);
        }

        return Task.CompletedTask;
    }

    public IEnumerable<Guid> GetAllClientIds() => _clients.Keys;
}

sealed class RoverState
{
    private readonly object _lock = new();
    private int _l, _r;
    private DateTime _lastUpdateUtc = DateTime.UtcNow;

    public (int L, int R, DateTime LastUpdateUtc) Get()
    {
        lock (_lock) return (_l, _r, _lastUpdateUtc);
    }

    public void Set(int l, int r)
    {
        lock (_lock)
        {
            _l = l;
            _r = r;
            _lastUpdateUtc = DateTime.UtcNow;
        }
    }

    public void Touch()
    {
        lock (_lock) _lastUpdateUtc = DateTime.UtcNow;
    }
}

sealed class OperatorManager
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<Guid, string> _clientNames = new();
    private int _nextPilotNumber = 1;

    private Guid? _currentOperatorId;
    private Guid? _pendingRequestId;
    private readonly ILogger<OperatorManager>? _logger;

    public OperatorManager(ILogger<OperatorManager>? logger = null)
    {
        _logger = logger;
    }

    public string RegisterClient(Guid clientId)
    {
        var name = $"Pilot-{_nextPilotNumber++}";
        _clientNames.TryAdd(clientId, name);
        _logger?.LogInformation($"Client registered: {clientId} as {name}");
        return name;
    }

    public void UnregisterClient(Guid clientId)
    {
        _clientNames.TryRemove(clientId, out _);

        lock (_lock)
        {
            if (_currentOperatorId == clientId)
            {
                _logger?.LogInformation($"Operator {clientId} disconnected, clearing operator");
                _currentOperatorId = null;
            }
            if (_pendingRequestId == clientId)
            {
                _logger?.LogInformation($"Requester {clientId} disconnected, clearing pending request");
                _pendingRequestId = null;
            }
        }
    }

    public string? GetClientName(Guid clientId)
    {
        return _clientNames.TryGetValue(clientId, out var name) ? name : null;
    }

    public bool IsOperator(Guid clientId)
    {
        lock (_lock) return _currentOperatorId == clientId;
    }

    public Guid? GetCurrentOperatorId()
    {
        lock (_lock) return _currentOperatorId;
    }

    public string? GetCurrentOperatorName()
    {
        lock (_lock)
        {
            if (_currentOperatorId.HasValue && _clientNames.TryGetValue(_currentOperatorId.Value, out var name))
                return name;
            return null;
        }
    }

    public bool TryClaim(Guid clientId)
    {
        lock (_lock)
        {
            if (_currentOperatorId == null)
            {
                _currentOperatorId = clientId;
                _logger?.LogInformation($"Client {clientId} claimed operator role");
                return true;
            }
            return false;
        }
    }

    public bool RequestControl(Guid clientId, out Guid? operatorId)
    {
        lock (_lock)
        {
            operatorId = _currentOperatorId;

            if (_currentOperatorId == null)
            {
                // No operator, auto-grant
                _currentOperatorId = clientId;
                _logger?.LogInformation($"No operator, auto-granting to {clientId}");
                return true;
            }

            if (_currentOperatorId == clientId)
            {
                // Already operator
                return true;
            }

            if (_pendingRequestId != null)
            {
                // Already a pending request
                _logger?.LogInformation($"Request from {clientId} rejected - already pending request from {_pendingRequestId}");
                return false;
            }

            _pendingRequestId = clientId;
            _logger?.LogInformation($"Control request from {clientId} pending for operator {_currentOperatorId}");
            return false;
        }
    }

    public (bool Success, Guid? NewOperatorId, Guid? OldOperatorId) AcceptRequest(Guid operatorId)
    {
        lock (_lock)
        {
            if (_currentOperatorId != operatorId)
            {
                _logger?.LogWarning($"Accept from non-operator {operatorId}");
                return (false, null, null);
            }

            if (_pendingRequestId == null)
            {
                _logger?.LogWarning($"Accept but no pending request");
                return (false, null, null);
            }

            var oldOperator = _currentOperatorId;
            var newOperator = _pendingRequestId.Value;
            _currentOperatorId = newOperator;
            _pendingRequestId = null;

            _logger?.LogInformation($"Control transferred from {oldOperator} to {newOperator}");
            return (true, newOperator, oldOperator);
        }
    }

    public (bool Success, Guid? RequesterId) DenyRequest(Guid operatorId)
    {
        lock (_lock)
        {
            if (_currentOperatorId != operatorId)
            {
                _logger?.LogWarning($"Deny from non-operator {operatorId}");
                return (false, null);
            }

            if (_pendingRequestId == null)
            {
                _logger?.LogWarning($"Deny but no pending request");
                return (false, null);
            }

            var requesterId = _pendingRequestId.Value;
            _pendingRequestId = null;

            _logger?.LogInformation($"Request from {requesterId} denied by {operatorId}");
            return (true, requesterId);
        }
    }

    public bool ReleaseControl(Guid operatorId)
    {
        lock (_lock)
        {
            if (_currentOperatorId != operatorId)
            {
                _logger?.LogWarning($"Release from non-operator {operatorId}");
                return false;
            }

            _logger?.LogInformation($"Operator {operatorId} released control");
            _currentOperatorId = null;
            _pendingRequestId = null; // Clear any pending request
            return true;
        }
    }

    public Guid? GetPendingRequestId()
    {
        lock (_lock) return _pendingRequestId;
    }

    public string? GetPendingRequesterName()
    {
        lock (_lock)
        {
            if (_pendingRequestId.HasValue && _clientNames.TryGetValue(_pendingRequestId.Value, out var name))
                return name;
            return null;
        }
    }
}

sealed class SerialPump : BackgroundService
{
    private readonly RoverState _state;
    private readonly SafetyStateMachine _safetyMachine;
    private readonly WebSocketManager _wsManager;
    private readonly ILogger<SerialPump>? _logger;
    private SerialPort? _sp;

    // Tunables
    private const string PortName = "/dev/serial0";
    private const int Baud = 115200;

    private const int SendHz = 20;                 // command stream rate
    private static readonly TimeSpan IdleStop = TimeSpan.FromMilliseconds(250); // stop if GUI silent

    public SerialPump(RoverState state, SafetyStateMachine safetyMachine, WebSocketManager wsManager, ILogger<SerialPump>? logger = null)
    {
        _state = state;
        _safetyMachine = safetyMachine;
        _wsManager = wsManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMilliseconds(1000.0 / SendHz);

        // Try to open serial port, but don't block startup if it fails
        bool serialAvailable = false;
        try
        {
            _sp = new SerialPort(PortName, Baud)
            {
                NewLine = "\n",
                DtrEnable = false,
                RtsEnable = false
            };
            _sp.Open();
            serialAvailable = true;
            _logger?.LogInformation("Serial port opened successfully");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Serial port not available, continuing without it");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var (l, r, last) = _state.Get();
            var age = DateTime.UtcNow - last;

            // Check if motors are inhibited by safety state machine
            if (_safetyMachine.AreMotorsInhibited())
            {
                // Force stop - safety override
                l = 0;
                r = 0;
            }
            else if (age > IdleStop)
            {
                // Normal idle timeout
                l = 0;
                r = 0;
            }

            // Always stream (keeps Uno watchdog happy)
            var cmd = $"M {l} {r}\n";

            // Try to write to serial if available
            if (serialAvailable && _sp != null)
            {
                try
                {
                    _sp.Write(cmd);
                }
                catch (Exception)
                {
                    // Serial error, mark as unavailable
                    serialAvailable = false;
                    _logger?.LogError("Serial port error, marking as unavailable");
                }
            }

            await Task.Delay(period, stoppingToken);
        }
    }

    /// <summary>
    /// Send immediate stop command (used by safety state machine during transitions)
    /// </summary>
    public void SendImmediateStop()
    {
        if (_sp != null)
        {
            try
            {
                _sp.Write("S\n");
                _logger?.LogWarning("IMMEDIATE STOP sent to UNO");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send immediate stop");
            }
        }
    }

    public override void Dispose()
    {
        try { _sp?.Close(); } catch { }
        _sp?.Dispose();
        base.Dispose();
    }
}

sealed class GpioController : IDisposable
{
    private const int HeadlightPin = 4;
    private System.Device.Gpio.GpioController? _controller;
    private bool _gpioAvailable = false;
    private bool _headlightState = false;
    private readonly ILogger<GpioController>? _logger;

    public GpioController(ILogger<GpioController>? logger = null)
    {
        _logger = logger;

        try
        {
            _controller = new System.Device.Gpio.GpioController();
            _controller.OpenPin(HeadlightPin, PinMode.Output);
            _controller.Write(HeadlightPin, PinValue.Low);
            _gpioAvailable = true;
            _logger?.LogInformation($"GPIO initialized successfully. Pin {HeadlightPin} set to output.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "GPIO not available, headlight control disabled");
            _gpioAvailable = false;
            _controller?.Dispose();
            _controller = null;
        }
    }

    public void SetHeadlight(bool on)
    {
        _headlightState = on;

        if (!_gpioAvailable || _controller == null)
        {
            _logger?.LogWarning("GPIO not available, cannot set headlight");
            return;
        }

        try
        {
            var pinValue = on ? PinValue.High : PinValue.Low;
            _controller.Write(HeadlightPin, pinValue);
            _logger?.LogInformation($"Headlight turned {(on ? "ON" : "OFF")} (GPIO {HeadlightPin} = {(on ? "HIGH" : "LOW")})");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to set GPIO");
        }
    }

    public bool GetHeadlightState() => _headlightState;

    public void Dispose()
    {
        if (_gpioAvailable && _controller != null)
        {
            try
            {
                // Turn off headlight
                _controller.Write(HeadlightPin, PinValue.Low);
                _controller.ClosePin(HeadlightPin);
            }
            catch { }

            _controller?.Dispose();
        }
    }
}

// ===== Wi-Fi Safety State Machine =====

/// <summary>
/// Wi-Fi connection safety states for motor gating
/// </summary>
enum SafetyState
{
    OK,       // Normal operation
    DEGRADED, // Link getting bad - warning mode
    ROAMING,  // AP switch in progress - STOP motors
    OFFLINE   // No connectivity - STOP motors
}

/// <summary>
/// Holds current Wi-Fi connection state, updated by WifiMonitor
/// </summary>
sealed class WifiState
{
    private readonly object _lock = new();

    // Current values
    private int _rssiDbm = -100;
    private string _bssid = "";
    private string _ssid = "";
    private int _freqMhz = 0;
    private double _txBitrateMbps = 0;
    private double _rxBitrateMbps = 0;
    private bool _isConnected = false;
    private DateTime _lastUpdated = DateTime.UtcNow;
    private DateTime _lastRoamAt = DateTime.MinValue;
    private string _previousBssid = "";

    // RTT tracking for degraded detection
    private readonly Queue<long> _rttHistory = new();
    private const int RTT_HISTORY_SIZE = 10;

    public void Update(int rssiDbm, string bssid, string ssid, int freqMhz, double txMbps, double rxMbps, bool connected)
    {
        lock (_lock)
        {
            // Detect BSSID change (roaming)
            if (!string.IsNullOrEmpty(_bssid) && !string.IsNullOrEmpty(bssid) &&
                _bssid != bssid && connected)
            {
                _previousBssid = _bssid;
                _lastRoamAt = DateTime.UtcNow;
            }

            _rssiDbm = rssiDbm;
            _bssid = bssid ?? "";
            _ssid = ssid ?? "";
            _freqMhz = freqMhz;
            _txBitrateMbps = txMbps;
            _rxBitrateMbps = rxMbps;
            _isConnected = connected;
            _lastUpdated = DateTime.UtcNow;
        }
    }

    public void AddRttSample(long rttMs)
    {
        lock (_lock)
        {
            _rttHistory.Enqueue(rttMs);
            while (_rttHistory.Count > RTT_HISTORY_SIZE)
                _rttHistory.Dequeue();
        }
    }

    public (int RssiDbm, string Bssid, string Ssid, int FreqMhz, double TxMbps, double RxMbps,
            bool IsConnected, DateTime LastUpdated, DateTime LastRoamAt, long AvgRttMs) Get()
    {
        lock (_lock)
        {
            var avgRtt = _rttHistory.Count > 0 ? (long)_rttHistory.Average() : 0;
            return (_rssiDbm, _bssid, _ssid, _freqMhz, _txBitrateMbps, _rxBitrateMbps,
                    _isConnected, _lastUpdated, _lastRoamAt, avgRtt);
        }
    }

    public bool DetectRoamingInProgress()
    {
        lock (_lock)
        {
            // Consider roaming in progress if BSSID changed within last 2 seconds
            return (DateTime.UtcNow - _lastRoamAt).TotalSeconds < 2.0;
        }
    }
}

/// <summary>
/// Safety state machine that gates motor commands based on Wi-Fi state
/// </summary>
sealed class SafetyStateMachine
{
    private readonly object _lock = new();
    private readonly WifiState _wifiState;
    private readonly RoverState _roverState;
    private readonly ILogger<SafetyStateMachine>? _logger;

    private SafetyState _currentState = SafetyState.OFFLINE;
    private DateTime _degradedSince = DateTime.MinValue;
    private DateTime _lastStopSentAt = DateTime.MinValue;
    private string _lastStopReason = "startup";
    private bool _motorsInhibited = true; // Start inhibited until we confirm connectivity

    // Thresholds from requirements
    private const int RSSI_DEGRADED_THRESHOLD = -67;  // dBm
    private const int RSSI_CRITICAL_THRESHOLD = -72;  // dBm
    private const int RTT_DEGRADED_THRESHOLD = 250;   // ms
    private const double DEGRADED_DURATION_SEC = 2.0;
    private const double STABLE_AFTER_ROAM_SEC = 1.0;

    public SafetyStateMachine(WifiState wifiState, RoverState roverState, ILogger<SafetyStateMachine>? logger = null)
    {
        _wifiState = wifiState;
        _roverState = roverState;
        _logger = logger;
    }

    /// <summary>
    /// Evaluates current Wi-Fi state and transitions the state machine.
    /// Returns true if a STOP command should be sent to the UNO.
    /// </summary>
    public (SafetyState State, bool ShouldSendStop, string StopReason) Evaluate()
    {
        lock (_lock)
        {
            var (rssi, bssid, ssid, freq, tx, rx, connected, lastUpdate, lastRoam, avgRtt) = _wifiState.Get();
            var previousState = _currentState;
            var shouldSendStop = false;
            var stopReason = "";

            // Check for stale data (Wi-Fi monitor not responding)
            var dataAge = DateTime.UtcNow - lastUpdate;
            if (dataAge.TotalSeconds > 5)
            {
                connected = false;
            }

            // State transitions
            if (!connected)
            {
                // ANY → OFFLINE
                if (_currentState != SafetyState.OFFLINE)
                {
                    _currentState = SafetyState.OFFLINE;
                    shouldSendStop = true;
                    stopReason = "offline";
                    _logger?.LogWarning("Safety: Transition to OFFLINE - Wi-Fi disconnected");
                }
            }
            else if (_wifiState.DetectRoamingInProgress())
            {
                // DEGRADED/OK → ROAMING
                if (_currentState != SafetyState.ROAMING && _currentState != SafetyState.OFFLINE)
                {
                    _currentState = SafetyState.ROAMING;
                    shouldSendStop = true;
                    stopReason = "roaming";
                    _logger?.LogWarning($"Safety: Transition to ROAMING - BSSID change detected");
                }
            }
            else if (rssi <= RSSI_CRITICAL_THRESHOLD)
            {
                // Critical RSSI - immediate degraded (from OK or OFFLINE)
                if (_currentState == SafetyState.OK || _currentState == SafetyState.OFFLINE)
                {
                    if (_currentState == SafetyState.OFFLINE)
                    {
                        _logger?.LogInformation($"Safety: Transition OFFLINE → DEGRADED - Connected but critical RSSI {rssi} dBm");
                    }
                    else
                    {
                        _logger?.LogWarning($"Safety: Transition to DEGRADED - Critical RSSI {rssi} dBm");
                    }
                    _currentState = SafetyState.DEGRADED;
                    _degradedSince = DateTime.UtcNow;
                }
            }
            else if (rssi <= RSSI_DEGRADED_THRESHOLD || avgRtt > RTT_DEGRADED_THRESHOLD)
            {
                // Check if we should transition to DEGRADED
                if (_currentState == SafetyState.OK || _currentState == SafetyState.OFFLINE)
                {
                    if (_currentState == SafetyState.OFFLINE)
                    {
                        // When coming from OFFLINE, go to DEGRADED immediately (we're connected!)
                        _currentState = SafetyState.DEGRADED;
                        _degradedSince = DateTime.UtcNow;
                        _logger?.LogInformation($"Safety: Transition OFFLINE → DEGRADED - Connected, RSSI {rssi} dBm");
                    }
                    else if (_degradedSince == DateTime.MinValue)
                    {
                        _degradedSince = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _degradedSince).TotalSeconds >= DEGRADED_DURATION_SEC)
                    {
                        _currentState = SafetyState.DEGRADED;
                        _logger?.LogWarning($"Safety: Transition to DEGRADED - RSSI {rssi} dBm, RTT {avgRtt} ms");
                    }
                }
            }
            else
            {
                // Good signal - check for recovery
                _degradedSince = DateTime.MinValue;

                if (_currentState == SafetyState.ROAMING)
                {
                    // ROAMING → OK: stable connected for 1s after roam
                    var timeSinceRoam = (DateTime.UtcNow - lastRoam).TotalSeconds;
                    if (timeSinceRoam >= STABLE_AFTER_ROAM_SEC)
                    {
                        _currentState = SafetyState.OK;
                        _logger?.LogInformation($"Safety: Transition to OK - Roaming complete, new BSSID {bssid}");
                    }
                }
                else if (_currentState == SafetyState.DEGRADED || _currentState == SafetyState.OFFLINE)
                {
                    _currentState = SafetyState.OK;
                    _logger?.LogInformation($"Safety: Transition to OK - RSSI {rssi} dBm");
                }
            }

            // Update motor inhibition
            _motorsInhibited = _currentState == SafetyState.ROAMING || _currentState == SafetyState.OFFLINE;

            // Send stop only once per transition
            if (shouldSendStop && (DateTime.UtcNow - _lastStopSentAt).TotalMilliseconds > 500)
            {
                _lastStopSentAt = DateTime.UtcNow;
                _lastStopReason = stopReason;
                _roverState.Set(0, 0); // Stop motors in state
                return (_currentState, true, stopReason);
            }

            return (_currentState, false, _lastStopReason);
        }
    }

    public (SafetyState State, bool MotorsInhibited, string LastStopReason) GetStatus()
    {
        lock (_lock)
        {
            return (_currentState, _motorsInhibited, _lastStopReason);
        }
    }

    public bool AreMotorsInhibited()
    {
        lock (_lock) return _motorsInhibited;
    }
}

/// <summary>
/// Background service that monitors Wi-Fi connection state
/// </summary>
sealed class WifiMonitor : BackgroundService
{
    private readonly WifiState _wifiState;
    private readonly ILogger<WifiMonitor> _logger;

    public WifiMonitor(WifiState wifiState, ILogger<WifiMonitor> logger)
    {
        _wifiState = wifiState;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WifiMonitor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var wifiInfo = await GetWifiInfoAsync();
                _wifiState.Update(
                    wifiInfo.RssiDbm,
                    wifiInfo.Bssid,
                    wifiInfo.Ssid,
                    wifiInfo.FreqMhz,
                    wifiInfo.TxMbps,
                    wifiInfo.RxMbps,
                    wifiInfo.IsConnected
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading Wi-Fi state");
                // Mark as disconnected on error
                _wifiState.Update(-100, "", "", 0, 0, 0, false);
            }

            await Task.Delay(250, stoppingToken); // Update at 4Hz
        }
    }

    private async Task<WifiInfo> GetWifiInfoAsync()
    {
        var info = new WifiInfo();

        try
        {
            // Use 'iw' command for detailed Wi-Fi info
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/iw",
                Arguments = "dev wlan0 link",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (output.Contains("Not connected"))
                {
                    info.IsConnected = false;
                    return info;
                }

                info.IsConnected = true;

                // Parse BSSID
                var bssidMatch = System.Text.RegularExpressions.Regex.Match(output, @"Connected to ([0-9a-fA-F:]+)");
                if (bssidMatch.Success)
                    info.Bssid = bssidMatch.Groups[1].Value.ToUpper();

                // Parse SSID
                var ssidMatch = System.Text.RegularExpressions.Regex.Match(output, @"SSID: (.+)");
                if (ssidMatch.Success)
                    info.Ssid = ssidMatch.Groups[1].Value.Trim();

                // Parse frequency
                var freqMatch = System.Text.RegularExpressions.Regex.Match(output, @"freq: (\d+)");
                if (freqMatch.Success && int.TryParse(freqMatch.Groups[1].Value, out var freq))
                    info.FreqMhz = freq;

                // Parse signal strength
                var signalMatch = System.Text.RegularExpressions.Regex.Match(output, @"signal: (-?\d+)");
                if (signalMatch.Success && int.TryParse(signalMatch.Groups[1].Value, out var rssi))
                    info.RssiDbm = rssi;

                // Parse TX bitrate
                var txMatch = System.Text.RegularExpressions.Regex.Match(output, @"tx bitrate: ([\d.]+)");
                if (txMatch.Success && double.TryParse(txMatch.Groups[1].Value, out var tx))
                    info.TxMbps = tx;

                // Parse RX bitrate
                var rxMatch = System.Text.RegularExpressions.Regex.Match(output, @"rx bitrate: ([\d.]+)");
                if (rxMatch.Success && double.TryParse(rxMatch.Groups[1].Value, out var rx))
                    info.RxMbps = rx;
            }
        }
        catch
        {
            // Fallback: try reading from /proc/net/wireless for basic RSSI
            try
            {
                if (File.Exists("/proc/net/wireless"))
                {
                    var lines = await File.ReadAllLinesAsync("/proc/net/wireless");
                    foreach (var line in lines)
                    {
                        if (line.Contains("wlan0"))
                        {
                            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 4 && int.TryParse(parts[3].TrimEnd('.'), out var level))
                            {
                                info.RssiDbm = level;
                                info.IsConnected = true;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        return info;
    }

    private record WifiInfo
    {
        public int RssiDbm { get; set; } = -100;
        public string Bssid { get; set; } = "";
        public string Ssid { get; set; } = "";
        public int FreqMhz { get; set; } = 0;
        public double TxMbps { get; set; } = 0;
        public double RxMbps { get; set; } = 0;
        public bool IsConnected { get; set; } = false;
    }

    /// <summary>
    /// Get list of nearby APs from wpa_cli scan_results (called less frequently)
    /// </summary>
    public async Task<List<NearbyAp>> GetNearbyApsAsync(string currentSsid)
    {
        var aps = new List<NearbyAp>();

        try
        {
            // Trigger a scan first
            var scanPsi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/wpa_cli",
                Arguments = "-i wlan0 scan",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var scanProcess = Process.Start(scanPsi))
            {
                if (scanProcess != null)
                    await scanProcess.WaitForExitAsync();
            }

            await Task.Delay(800); // Wait for scan to complete

            // Get scan results
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/wpa_cli",
                Arguments = "-i wlan0 scan_results",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // Parse: bssid / frequency / signal level / flags / ssid
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines.Skip(1)) // Skip header
                {
                    var parts = line.Split('\t');
                    if (parts.Length >= 4)
                    {
                        var apSsid = parts.Length >= 5 ? parts[4].Trim() : "";

                        // Only include APs with matching SSID or empty SSID (mesh nodes)
                        // Use case-insensitive comparison
                        if (!string.IsNullOrEmpty(currentSsid) &&
                            !apSsid.Equals(currentSsid, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrEmpty(apSsid))
                            continue;

                        if (int.TryParse(parts[2], out var apRssi))
                        {
                            aps.Add(new NearbyAp
                            {
                                Bssid = parts[0].ToUpper(),
                                FreqMhz = int.TryParse(parts[1], out var f) ? f : 0,
                                RssiDbm = apRssi,
                                Ssid = apSsid
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting nearby APs");
        }

        // Sort by signal strength (best first)
        return aps.OrderByDescending(a => a.RssiDbm).Take(5).ToList();
    }

    public record NearbyAp
    {
        public string Bssid { get; set; } = "";
        public string Ssid { get; set; } = "";
        public int RssiDbm { get; set; } = -100;
        public int FreqMhz { get; set; } = 0;
    }
}

sealed class DiagnosticsPump : BackgroundService
{
    private readonly WebSocketManager _wsManager;
    private readonly WifiState _wifiState;
    private readonly WifiMonitor _wifiMonitor;
    private readonly SafetyStateMachine _safetyMachine;
    private readonly ILogger<DiagnosticsPump> _logger;

    public DiagnosticsPump(WebSocketManager wsManager, WifiState wifiState, WifiMonitor wifiMonitor, SafetyStateMachine safetyMachine, ILogger<DiagnosticsPump> logger)
    {
        _wsManager = wsManager;
        _wifiState = wifiState;
        _wifiMonitor = wifiMonitor;
        _safetyMachine = safetyMachine;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ping = new System.Net.NetworkInformation.Ping();
        var updateCounter = 0;
        long lastPingMs = -1; // Cache last ping result
        List<WifiMonitor.NearbyAp> nearbyAps = new(); // Cache nearby APs

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Evaluate safety state machine
                var (safetyState, shouldStop, stopReason) = _safetyMachine.Evaluate();

                var cpuTemp = GetCpuTemp();

                // Only ping every 8th iteration (~2 seconds at 4Hz) to reduce network overhead
                if (updateCounter % 8 == 0)
                {
                    lastPingMs = await GetPingAsync(ping);

                    // Add RTT sample for safety state tracking
                    if (lastPingMs > 0)
                    {
                        _wifiState.AddRttSample(lastPingMs);
                    }
                }

                // Get Wi-Fi state
                var (rssi, bssid, ssid, freq, txMbps, rxMbps, connected, lastUpdate, lastRoam, avgRtt) = _wifiState.Get();
                var (_, motorsInhibited, lastStopReason) = _safetyMachine.GetStatus();

                // Scan for nearby APs every ~5 seconds (20 iterations at 4Hz)
                if (updateCounter % 20 == 0)
                {
                    try
                    {
                        nearbyAps = await _wifiMonitor.GetNearbyApsAsync(ssid);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error scanning nearby APs");
                    }
                }

                // Calculate Wi-Fi signal percentage for backwards compatibility (0-100%)
                var wifiSignalPercent = connected ? Math.Clamp((int)((rssi + 100) * 100.0 / 70.0), 0, 100) : 0;

                // Check if a better AP is available (not the current one, and significantly stronger)
                var betterApAvailable = nearbyAps
                    .Where(ap => ap.Bssid != bssid && ap.RssiDbm > rssi + 8)
                    .OrderByDescending(ap => ap.RssiDbm)
                    .FirstOrDefault();

                // Send full telemetry at 2-5 Hz (every 200-500ms), we'll do 4Hz
                // Format: TELEM:JSON
                var telemetry = new
                {
                    wifi = new
                    {
                        state = safetyState.ToString(),
                        connected = connected,
                        rssiDbm = rssi,
                        bssid = bssid,
                        ssid = ssid,
                        freqMhz = freq,
                        txBitrateMbps = txMbps,
                        rxBitrateMbps = rxMbps,
                        lastRoamAt = lastRoam != DateTime.MinValue ? lastRoam.ToString("o") : null,
                        signalPercent = wifiSignalPercent,
                        nearbyAps = nearbyAps.Select(ap => new
                        {
                            bssid = ap.Bssid,
                            ssid = ap.Ssid,
                            // Use live RSSI for current AP, scan cache for others
                            rssiDbm = ap.Bssid.Equals(bssid, StringComparison.OrdinalIgnoreCase) ? rssi : ap.RssiDbm,
                            isCurrent = ap.Bssid.Equals(bssid, StringComparison.OrdinalIgnoreCase)
                        }),
                        betterApAvailable = betterApAvailable != null ? new
                        {
                            bssid = betterApAvailable.Bssid,
                            rssiDbm = betterApAvailable.RssiDbm,
                            improvement = betterApAvailable.RssiDbm - rssi
                        } : null
                    },
                    motors = new
                    {
                        inhibited = motorsInhibited,
                        lastStopReason = lastStopReason
                    },
                    system = new
                    {
                        cpuTempC = cpuTemp,
                        pingMs = lastPingMs
                    }
                };

                var telemJson = JsonSerializer.Serialize(telemetry, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
                await _wsManager.BroadcastAsync($"TELEM:{telemJson}");

                // Also send legacy DIAG format for backwards compatibility
                if (updateCounter % 4 == 0) // Every ~1 second
                {
                    var diagMsg = $"DIAG:{cpuTemp:F1}|{wifiSignalPercent}|{lastPingMs}";
                    await _wsManager.BroadcastAsync(diagMsg);
                }

                updateCounter++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error gathering diagnostics");
            }

            await Task.Delay(250, stoppingToken); // Update at 4Hz
        }
    }

    private double GetCpuTemp()
    {
        try
        {
            if (File.Exists("/sys/class/thermal/thermal_zone0/temp"))
            {
                var tempStr = File.ReadAllText("/sys/class/thermal/thermal_zone0/temp").Trim();
                if (double.TryParse(tempStr, out var temp))
                {
                    return temp / 1000.0;
                }
            }
        }
        catch { }
        return 0;
    }

    private async Task<long> GetPingAsync(System.Net.NetworkInformation.Ping ping)
    {
        try
        {
            var reply = await ping.SendPingAsync("8.8.8.8", 1000);
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
        }
        catch { }
        return -1;
    }
}