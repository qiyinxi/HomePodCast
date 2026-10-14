namespace HomePodCast.VideoWindow;

/// <summary>
/// How long a frame takes from being chosen to being on screen, which the presenter subtracts from the picture delay
/// (choosing the frame due at now + this): measured from DXGI frame statistics (the vblank a present was shown at),
/// smoothed; until there are enough measurements, one refresh period (a windowed present is composed at the next one).
/// The first version subtracted nothing, so its picture was systematically late by its whole drawing path.
/// </summary>
internal sealed class DisplayLatency
{
    private const long MinPeriod = 20_000, MaxPeriod = 1_000_000;  // 2–100 ms: 10–500 Hz
    private const long MaxLatency = 1_000_000;                     // 100 ms: anything longer is not this path
    private const int Needed = 8;
    private readonly Queue<(uint Present, long ChosenAt)> _pending = new();
    private long _lastWake;
    private double _period, _latency;
    private int _skippedWakes;

    /// <summary>Measurements so far; <see cref="Ticks"/> uses them from <see cref="Needed"/> on.</summary>
    public int Samples { get; private set; }

    public bool Measured => Samples >= Needed;

    /// <summary>The refresh period seen between wake-ups, 0 until known.</summary>
    public long RefreshTicks => (long)_period;

    /// <summary>The estimate, 100 ns ticks.</summary>
    public long Ticks => Math.Clamp(Measured ? (long)_latency : _period > 0 ? (long)_period : 166_667, 0, MaxLatency);

    /// <summary>The presenter woke for a refresh (the swap chain's waitable object).</summary>
    public void Woke(long now)
    {
        long d = _lastWake == 0 ? 0 : now - _lastWake;
        _lastWake = now;
        if (d is < MinPeriod or > MaxPeriod) return;
        if (_period == 0 || _skippedWakes >= 30) { _period = d; _skippedWakes = 0; }
        else if (d < _period * 1.5) { _period += (d - _period) / 16; _skippedWakes = 0; }
        else _skippedWakes++; // a missed refresh, or a slower screen (taken over after 30 in a row)
    }

    /// <summary>Present number <paramref name="present"/> (GetLastPresentCount) showed the frame chosen at <paramref name="chosenAt"/>.</summary>
    public void Presented(uint present, long chosenAt)
    {
        _pending.Enqueue((present, chosenAt));
        while (_pending.Count > 32) _pending.Dequeue();
    }

    /// <summary>The frame statistics: present number <paramref name="present"/> reached the screen at <paramref name="shownAt"/>.</summary>
    public void Displayed(uint present, long shownAt)
    {
        while (_pending.TryPeek(out var p) && p.Present < present) _pending.Dequeue();
        if (!_pending.TryPeek(out var match) || match.Present != present) return;
        _pending.Dequeue();
        long d = shownAt - match.ChosenAt;
        if (d is <= 0 or > MaxLatency) return;
        _latency = Samples == 0 ? d : _latency + (d - _latency) / 8;
        Samples++;
    }
}
