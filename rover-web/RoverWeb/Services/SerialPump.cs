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

    private readonly RoverLogService? _logService;

    public SerialPump(RoverState state, SafetyStateMachine safetyMachine, WebSocketManager wsManager, ILogger<SerialPump>? logger = null, RoverLogService? logService = null)
    {
        _state = state;
        _safetyMachine = safetyMachine;
        _wsManager = wsManager;
        _logger = logger;
        _logService = logService;
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
            _logService?.Publish("serial", "Serial port opened", PortName, "info");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Serial port not available, continuing without it");
            _logService?.Publish("serial", "Serial port unavailable", ex.Message, "warn");
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
                catch (Exception ex)
                {
                    // Serial error, mark as unavailable
                    serialAvailable = false;
                    _logger?.LogError("Serial port error, marking as unavailable");
                    _logService?.Publish("serial", "Serial port error", ex.Message, "error");
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
                _logService?.Publish("serial", "Immediate stop sent", "safety override", "warn");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to send immediate stop");
                _logService?.Publish("serial", "Failed to send immediate stop", ex.Message, "error");
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

