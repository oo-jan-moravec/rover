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
