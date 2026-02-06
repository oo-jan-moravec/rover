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

