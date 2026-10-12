namespace HomePodCast.VideoWindow;

/// <summary>Timestamped ownership transfer. Capacity pressure drops frames, never displays one early.</summary>
internal sealed class DelayQueue<T>(long byteLimit, int frameLimit) : IDisposable where T : class, IDisposable
{
    private readonly Queue<(long Time, long Bytes, T Value)> _frames = new();
    private readonly object _gate = new();
    private long _bytes, _lastTime = long.MinValue;
    private bool _disposed;
    public long Dropped { get; private set; }
    public (int Frames, long Bytes) Buffered { get { lock (_gate) return (_frames.Count, _bytes); } }

    public void Add(long time, long bytes, T value)
    {
        lock (_gate)
        {
            if (_disposed || bytes <= 0 || bytes > byteLimit || time <= _lastTime)
            { value.Dispose(); Dropped++; return; }
            _lastTime = time;
            while (_frames.Count >= frameLimit || _bytes + bytes > byteLimit) Drop();
            _frames.Enqueue((time, bytes, value)); _bytes += bytes;
        }
    }

    public T? TakeReady(long now, long delay)
    {
        lock (_gate)
        {
            T? latest = null;
            long due = now - Math.Max(0, delay);
            while (_frames.TryPeek(out var next) && next.Time <= due)
            {
                _frames.Dequeue(); _bytes -= next.Bytes;
                latest?.Dispose(); latest = next.Value;
            }
            return latest;
        }
    }

    private void Drop() { var old = _frames.Dequeue(); _bytes -= old.Bytes; old.Value.Dispose(); Dropped++; }
    public void Clear()
    {
        lock (_gate) { while (_frames.Count > 0) Drop(); _lastTime = long.MinValue; }
    }
    public void Dispose() { lock (_gate) { _disposed = true; Clear(); } }
}
