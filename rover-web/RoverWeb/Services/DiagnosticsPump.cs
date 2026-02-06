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

// ===== Audio Services =====

/// <summary>
/// Captures audio from USB soundcard microphone and broadcasts to all WebSocket clients
/// </summary>
