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

    // CRITICAL FIX: Add grace period before going OFFLINE to allow brief disconnections during roaming
    private DateTime? _disconnectedSince = null;
    private static readonly TimeSpan OFFLINE_GRACE_PERIOD = TimeSpan.FromSeconds(3); // Allow 3s for AP switching

    // Thresholds from requirements
    private const int RSSI_DEGRADED_THRESHOLD = -67;  // dBm
    private const int RSSI_CRITICAL_THRESHOLD = -72;  // dBm
    private const int RTT_DEGRADED_THRESHOLD = 250;   // ms
    private const double DEGRADED_DURATION_SEC = 2.0;
    private const double STABLE_AFTER_ROAM_SEC = 1.0;

    private readonly RoverLogService? _logService;

    public SafetyStateMachine(WifiState wifiState, RoverState roverState, ILogger<SafetyStateMachine>? logger = null, RoverLogService? logService = null)
    {
        _wifiState = wifiState;
        _roverState = roverState;
        _logger = logger;
        _logService = logService;
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
                // CRITICAL FIX: Don't immediately go OFFLINE - give grace period for AP switching
                if (_disconnectedSince == null)
                {
                    _disconnectedSince = DateTime.UtcNow;
                    _logger?.LogDebug("Safety: WiFi disconnected, starting grace period");
                }

                var disconnectedFor = DateTime.UtcNow - _disconnectedSince.Value;

                // Only transition to OFFLINE after grace period expires
                if (disconnectedFor >= OFFLINE_GRACE_PERIOD)
                {
                    // ANY → OFFLINE (but only after grace period)
                    if (_currentState != SafetyState.OFFLINE)
                    {
                        _currentState = SafetyState.OFFLINE;
                        shouldSendStop = true;
                        stopReason = "offline";
                        _logger?.LogWarning($"Safety: Transition to OFFLINE - disconnected for {(int)disconnectedFor.TotalSeconds}s");
                        _logService?.Publish("safety", "Safety state: OFFLINE", $"disconnected for {(int)disconnectedFor.TotalSeconds}s, motors inhibited", "error");
                    }
                }
                else
                {
                    // Still in grace period - log but don't change state yet
                    _logger?.LogDebug($"Safety: In grace period ({(int)disconnectedFor.TotalSeconds}s / {(int)OFFLINE_GRACE_PERIOD.TotalSeconds}s)");
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
                    _logService?.Publish("safety", "Safety state: ROAMING", "motors inhibited during AP switch", "warn");
                }
            }
            else if (rssi <= RSSI_CRITICAL_THRESHOLD)
            {
                // Connected - reset disconnected timer
                _disconnectedSince = null;

                // Critical RSSI - immediate degraded (from OK or OFFLINE)
                if (_currentState == SafetyState.OK || _currentState == SafetyState.OFFLINE)
                {
                    if (_currentState == SafetyState.OFFLINE)
                    {
                        _logger?.LogInformation($"Safety: Transition OFFLINE → DEGRADED - Connected but critical RSSI {rssi} dBm");
                        _logService?.Publish("safety", "Safety state: DEGRADED", $"Connected but critical RSSI {rssi} dBm", "warn");
                    }
                    else
                    {
                        _logger?.LogWarning($"Safety: Transition to DEGRADED - Critical RSSI {rssi} dBm");
                        _logService?.Publish("safety", "Safety state: DEGRADED", $"Critical RSSI {rssi} dBm", "warn");
                    }
                    _currentState = SafetyState.DEGRADED;
                    _degradedSince = DateTime.UtcNow;
                }
            }
            else if (rssi <= RSSI_DEGRADED_THRESHOLD || avgRtt > RTT_DEGRADED_THRESHOLD)
            {
                // Connected - reset disconnected timer
                _disconnectedSince = null;

                // Check if we should transition to DEGRADED
                if (_currentState == SafetyState.OK || _currentState == SafetyState.OFFLINE)
                {
                    if (_currentState == SafetyState.OFFLINE)
                    {
                        // When coming from OFFLINE, go to DEGRADED immediately (we're connected!)
                        _currentState = SafetyState.DEGRADED;
                        _degradedSince = DateTime.UtcNow;
                        _logger?.LogInformation($"Safety: Transition OFFLINE → DEGRADED - Connected, RSSI {rssi} dBm");
                        _logService?.Publish("safety", "Safety state: DEGRADED", $"Connected, RSSI {rssi} dBm", "info");
                    }
                    else if (_degradedSince == DateTime.MinValue)
                    {
                        _degradedSince = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - _degradedSince).TotalSeconds >= DEGRADED_DURATION_SEC)
                    {
                        _currentState = SafetyState.DEGRADED;
                        _logger?.LogWarning($"Safety: Transition to DEGRADED - RSSI {rssi} dBm, RTT {avgRtt} ms");
                        _logService?.Publish("safety", "Safety state: DEGRADED", $"RSSI {rssi} dBm, RTT {avgRtt} ms", "warn");
                    }
                }
            }
            else
            {
                // Connected with good signal - reset disconnected timer
                _disconnectedSince = null;

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
                        _logService?.Publish("safety", "Safety state: OK", $"Roaming complete, motors enabled", "info");
                    }
                }
                else if (_currentState == SafetyState.DEGRADED || _currentState == SafetyState.OFFLINE)
                {
                    if (previousState != SafetyState.OK)
                    {
                        _logService?.Publish("safety", "Safety state: OK", $"RSSI {rssi} dBm, motors enabled", "info");
                    }
                    _currentState = SafetyState.OK;
                    _logger?.LogInformation($"Safety: Transition to OK - RSSI {rssi} dBm");
                }
            }

            // Update motor inhibition
            var wasInhibited = _motorsInhibited;
            _motorsInhibited = _currentState == SafetyState.ROAMING || _currentState == SafetyState.OFFLINE;

            // Log when motors are enabled after being inhibited
            if (wasInhibited && !_motorsInhibited && previousState != _currentState)
            {
                _logService?.Publish("safety", "Motors enabled", $"safety state: {_currentState}", "info");
            }

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

enum SafetyState
{
    OFFLINE,
    ROAMING,
    DEGRADED,
    OK
}

/// <summary>
/// Background service that monitors Wi-Fi connection state
/// </summary>
