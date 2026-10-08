namespace HomePodCast;

/// <summary>
/// Runs requests one at a time on the thread pool, the latest one winning: a request made while another runs
/// waits for it, and replaces any request still waiting. The UI's Connect and Disconnect go through one, so the
/// UI thread never waits for StreamController.Stop (up to 3 s for the stream loop, plus the capture's teardown),
/// and whatever was asked last is what the controller ends up doing (Disconnect then Connect ends connected).
/// </summary>
internal sealed class LatestRequestQueue
{
    private readonly object _lock = new();
    private readonly string _name;
    private Action? _pending;
    private Task _worker = Task.CompletedTask;
    private bool _running, _closed;

    public LatestRequestQueue(string name) => _name = name;

    /// <summary>
    /// Queue <paramref name="request"/> in place of any request still waiting. The task completes once the queue
    /// is idle again (this request, or one that replaced it, has run); it never faults.
    /// </summary>
    public Task Post(Action request)
    {
        lock (_lock)
        {
            if (_closed) return Task.CompletedTask;
            _pending = request;
            if (!_running)
            {
                _running = true;
                _worker = Task.Run(Drain);
            }
            return _worker;
        }
    }

    /// <summary>
    /// Drop the waiting request and refuse new ones (quitting). Returns the task of the request still running, if
    /// any, so the caller can let it finish before tearing down what it works on.
    /// </summary>
    public Task Close()
    {
        lock (_lock)
        {
            _closed = true;
            _pending = null;
            return _worker;
        }
    }

    private void Drain()
    {
        while (true)
        {
            Action next;
            lock (_lock)
            {
                if (_pending == null)
                {
                    _running = false;
                    return;
                }
                next = _pending;
                _pending = null;
            }
            try
            {
                next();
            }
            catch (Exception ex)
            {
                Log.Warn($"{_name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
