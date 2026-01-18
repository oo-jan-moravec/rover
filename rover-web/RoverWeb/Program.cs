using System.Collections.Concurrent;
using System.Device.Gpio;
using System.IO.Ports;
using System.Net.WebSockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RoverState>();
builder.Services.AddSingleton<WebSocketManager>();
builder.Services.AddSingleton<GpioController>();
builder.Services.AddHostedService<SerialPump>();
builder.Services.AddHostedService<DiagnosticsPump>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseWebSockets();

app.Map("/ws", async (HttpContext ctx, RoverState state, WebSocketManager wsManager, GpioController gpio) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = 400;
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var clientId = Guid.NewGuid();
    wsManager.AddClient(clientId, ws);

    try
    {
        var buffer = new byte[256];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close) break;

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count).Trim();

            state.Touch();

            if (msg == "S")
            {
                state.Set(0, 0);
            }
            else if (msg.StartsWith("M "))
            {
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
        }
    }
    finally
    {
        wsManager.RemoveClient(clientId);
        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); }
        catch { /* ignore */ }
    }
});

app.Run("http://0.0.0.0:8080");

static int Clamp(int v) => Math.Max(-255, Math.Min(255, v));

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

sealed class SerialPump : BackgroundService
{
    private readonly RoverState _state;
    private readonly WebSocketManager _wsManager;
    private SerialPort? _sp;

    // Tunables
    private const string PortName = "/dev/serial0";
    private const int Baud = 115200;

    private const int SendHz = 20;                 // command stream rate
    private static readonly TimeSpan IdleStop = TimeSpan.FromMilliseconds(250); // stop if GUI silent

    public SerialPump(RoverState state, WebSocketManager wsManager)
    {
        _state = state;
        _wsManager = wsManager;
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
        }
        catch (Exception)
        {
            // Serial port not available, continue without it
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var (l, r, last) = _state.Get();
            var age = DateTime.UtcNow - last;

            if (age > IdleStop)
            {
                l = 0; r = 0;
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
                }
            }

            await Task.Delay(period, stoppingToken);
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

sealed class DiagnosticsPump : BackgroundService
{
    private readonly WebSocketManager _wsManager;
    private readonly ILogger<DiagnosticsPump> _logger;

    public DiagnosticsPump(WebSocketManager wsManager, ILogger<DiagnosticsPump> logger)
    {
        _wsManager = wsManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ping = new System.Net.NetworkInformation.Ping();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var cpuTemp = GetCpuTemp();
                var wifiSignal = GetWifiSignal();
                var pingMs = await GetPingAsync(ping);

                var diagMsg = $"DIAG:{cpuTemp:F1}|{wifiSignal}|{pingMs}";
                await _wsManager.BroadcastAsync(diagMsg);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error gathering diagnostics");
            }

            await Task.Delay(1000, stoppingToken); // Update every second
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

    private int GetWifiSignal()
    {
        try
        {
            if (File.Exists("/proc/net/wireless"))
            {
                var lines = File.ReadAllLines("/proc/net/wireless");
                foreach (var line in lines)
                {
                    if (line.Contains("wlan0"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4 && int.TryParse(parts[3].TrimEnd('.'), out var level))
                        {
                            // Level is usually in dBm (e.g., -50)
                            // Map -100 to -30 to 0% to 100%
                            return Math.Clamp((int)((level + 100) * 100.0 / 70.0), 0, 100);
                        }
                    }
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