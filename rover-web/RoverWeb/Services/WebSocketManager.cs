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

    public Task BroadcastAudioAsync(byte[] audioData)
    {
        if (_clients.IsEmpty) return Task.CompletedTask;

        var deadClients = new List<Guid>();

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
                        await ws.SendAsync(audioData, WebSocketMessageType.Binary, true, CancellationToken.None);
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

