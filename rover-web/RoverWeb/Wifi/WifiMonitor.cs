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
                // _wifiState.Update(-100, "", "", 0, 0, 0, false);
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

