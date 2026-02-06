sealed class RoverLogService
{
    private readonly WebSocketManager _wsManager;
    private readonly ILogger<RoverLogService> _logger;
    private readonly Queue<LogEntry> _history = new();
    private readonly object _lock = new();
    private const int MaxEntries = 100;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public RoverLogService(WebSocketManager wsManager, ILogger<RoverLogService> logger)
    {
        _wsManager = wsManager;
        _logger = logger;
    }

    public void Publish(string category, string message, string? detail = null, string level = "info")
    {
        var entry = new LogEntry
        {
            Timestamp = DateTime.UtcNow.ToString("o"),
            Category = category,
            Message = message,
            Detail = detail,
            Level = level
        };

        lock (_lock)
        {
            _history.Enqueue(entry);
            while (_history.Count > MaxEntries)
            {
                _history.Dequeue();
            }
        }

        var payload = JsonSerializer.Serialize(entry, _jsonOptions);
        _ = _wsManager.BroadcastAsync($"LOG:{payload}");
        _logger.LogInformation("[{Category}] {Message} {Detail}", category, message, detail);
    }

    public async Task SendHistoryAsync(Guid clientId, int maxEntries = 20)
    {
        List<LogEntry> snapshot;
        lock (_lock)
        {
            snapshot = _history.Skip(Math.Max(0, _history.Count - maxEntries)).ToList();
        }

        if (snapshot.Count == 0) return;

        var payload = JsonSerializer.Serialize(snapshot, _jsonOptions);
        await _wsManager.SendToClientAsync(clientId, $"LOGH:{payload}");
    }

    public record LogEntry
    {
        public string Timestamp { get; init; } = "";
        public string Category { get; init; } = "";
        public string Message { get; init; } = "";
        public string? Detail { get; init; }
        public string Level { get; init; } = "info";
    }
}

/// <summary>
/// Advanced WiFi roaming service with verified transitions and multiple methods
/// </summary>
