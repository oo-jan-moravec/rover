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

    /// <summary>
    /// Check if there is any operator
    /// </summary>
    public bool HasOperator()
    {
        lock (_lock) return _currentOperatorId.HasValue;
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

