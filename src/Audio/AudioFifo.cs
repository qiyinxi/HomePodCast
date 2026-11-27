using System.Diagnostics;

namespace HomePodCast.Audio;

/// <summary>
/// Interleaved float FIFO between capture (producer) and the RTP sender (consumer), at the stream rate: stereo, or 6
/// channels for a HomePod's 5.1 stream (experimental since 1.4). Sizes, targets and counts are in frames.
/// Keeps latency bounded: overflow trims back to the target; underflow pads silence and re-primes.
/// </summary>
/// <remarks>
/// An empty FIFO is only a dropout if audio that existed reached it late: WASAPI loopback stops delivering
/// altogether while nothing plays, so "the music was paused" also drains it. The FIFO therefore decides when
/// data resumes, by how long it was dry and by how much arrives:
/// <list type="bullet">
/// <item>later than <see cref="DropoutWindowMs"/>: the source went quiet (<see cref="IdleGaps"/>);</item>
/// <item>sooner, but dry for more than <see cref="SourcePauseMarginMs"/> longer than the audio the resuming write
/// brings: the source paused for a moment (<see cref="IdleGaps"/> as well);</item>
/// <item>otherwise a dropout (<see cref="Underruns"/>, and with an adaptive target the target grows).</item>
/// </list>
/// A FIFO can only absorb late delivery: audio that was produced on time and reached the capture late comes as a
/// backlog that covers the hole. When the source itself paused, nothing was late, only an ordinary packet follows,
/// and a larger buffer would have changed nothing. 2026-11-27, the maintainer's PC: at every track change the player
/// (a web player in msedge; NetEase Cloud Music alike) stops its audio stream for a moment, and Windows delivers no
/// packets meanwhile. The log had hundreds of capture holes just under 200 ms at song-length spacing, each followed by
/// one 10 ms packet; each counted as a dropout and took the target to its ceiling (50 to 100 ms of latency), for good.
/// </remarks>
public sealed partial class AudioFifo
{
    /// <summary>
    /// Data that resumes within this time after the FIFO ran dry can mean an audible dropout (see
    /// <see cref="SourcePauseMarginMs"/>); later, the source had gone quiet.
    /// </summary>
    public const int DropoutWindowMs = 200;

    /// <summary>
    /// A FIFO that was dry for more than this much longer than the audio the resuming write brings was not waiting for
    /// late audio: the source had paused (see the remarks). The margin is two 10 ms capture periods: a backlog may be a
    /// period short of the time that passed, and a sender that read a burst leaves the FIFO dry for a few ms before an
    /// ordinary packet; both stay dropouts.
    /// </summary>
    public const int SourcePauseMarginMs = 20;

    private readonly float[] _buf;
    private readonly int _capacity; // frames
    private readonly int _channels;
    private readonly int _msFrames;
    private readonly int _capMargin;
    private readonly object _lock = new();
    private int _read;              // frame index
    private int _count;             // frames
    private bool _priming = true;
    private bool _dryPending;       // ran dry; classified at the next write
    private long _dryAt;
    private long _cleanReads;
    private int _floorFrames;       // relaxing never goes below a level that already dropped out (+2 ms)
    private bool _reader = true;    // a sender has read since BeginSession
    private long _graceUntil = long.MinValue; // running dry before this is the sender's start, not a dropout

    public int TargetFrames { get; set; }
    public int CapFrames { get; set; }

    /// <summary>The configured target; an adaptive target never shrinks below it.</summary>
    public int BaseTargetFrames { get; }

    /// <summary>
    /// Adaptive target (0 = off): each dropout raises the target by <see cref="GrowMs"/> up to this many frames;
    /// <see cref="RelaxAfterReads"/> clean reads lower it by 1 ms again, down to <see cref="BaseTargetFrames"/>.
    /// Bursty capture devices end up with just enough buffer, steady ones keep the lowest latency.
    /// </summary>
    public int MaxTargetFrames { get; set; }

    public int GrowMs { get; set; } = 4;
    public long RelaxAfterReads { get; set; } = long.MaxValue;

    /// <summary>Audible dropouts: the FIFO ran dry while the source was still playing.</summary>
    public long Underruns { get; private set; }

    /// <summary>
    /// The FIFO ran dry because the source went quiet (paused, nothing playing, a player's short stop between two
    /// tracks): no dropout, and the target stays.
    /// </summary>
    public long IdleGaps { get; private set; }

    public long Overflows { get; private set; }
    public long DroppedFrames { get; private set; }

    /// <summary>Clock in Stopwatch ticks (tests replace it).</summary>
    internal Func<long> Clock { get; set; } = Stopwatch.GetTimestamp;

    public int Depth => Volatile.Read(ref _count);
    public double TargetMs => TargetFrames / (double)_msFrames;

    /// <summary>Interleaved channels per frame: 2, or 6 for 5.1.</summary>
    public int Channels => _channels;

    public int SampleRate { get; }

    public AudioFifo(int sampleRate, int targetMs, int capMs, int channels = 2)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        _capacity = sampleRate; // 1 s
        _channels = channels;
        SampleRate = sampleRate;
        _buf = new float[_capacity * channels];
        _msFrames = sampleRate / 1000;
        TargetFrames = BaseTargetFrames = sampleRate * targetMs / 1000;
        CapFrames = sampleRate * capMs / 1000;
        _capMargin = CapFrames - TargetFrames;
    }

    public void Write(ReadOnlySpan<float> interleaved)
    {
        int channels = _channels;
        int frames = interleaved.Length / channels;
        if (frames > _capacity)
        {
            interleaved = interleaved[^(_capacity * channels)..];
            frames = _capacity;
        }
        lock (_lock)
        {
            if (_dryPending) ClassifyDry(frames);
            if (_count + frames > _capacity) Skip(_count + frames - _capacity); // ring full: oldest go first
            int write = (_read + _count) % _capacity;
            if (channels == 2)
            {
                for (int i = 0; i < frames; i++)
                {
                    _buf[write * 2] = interleaved[i * 2];
                    _buf[write * 2 + 1] = interleaved[i * 2 + 1];
                    if (++write == _capacity) write = 0;
                }
            }
            else
            {
                for (int i = 0; i < frames; i++)
                {
                    for (int c = 0; c < channels; c++) _buf[write * channels + c] = interleaved[i * channels + c];
                    if (++write == _capacity) write = 0;
                }
            }
            _count += frames;

            if (_count > CapFrames)
            {
                // Too far ahead of the sender: jump back to the target so latency stays bounded.
                int drop = _count - TargetFrames;
                Skip(drop);
                if (_reader)
                {
                    Overflows++;
                    DroppedFrames += drop;
                }
            }
        }
    }

    /// <summary>
    /// A session is connecting (first connect, reconnect, scene change). Measured 2026-10-08: without this, each
    /// scene change counted a dropout and grew the target by 4 ms. Until the new sender's first read, trimming is
    /// just the connect wait (not counted); during its first <see cref="SessionGraceMs"/> running dry is its start-up
    /// burst (neither a dropout nor an idle gap); a dry spell left over from the old session is forgotten.
    /// </summary>
    public void BeginSession()
    {
        lock (_lock)
        {
            _reader = false;
            _dryPending = false;
        }
    }

    public const int SessionGraceMs = 1000;

    /// <summary>Fill dest (frames × <see cref="Channels"/> floats). Returns false if any silence had to be inserted.</summary>
    public bool Read(Span<float> dest)
    {
        int channels = _channels;
        int frames = dest.Length / channels;
        lock (_lock)
        {
            if (!_reader)
            {
                _reader = true;
                _graceUntil = Clock() + SessionGraceMs * Stopwatch.Frequency / 1000;
            }
            if (_priming)
            {
                if (_count < TargetFrames)
                {
                    dest.Clear();
                    return false;
                }
                _priming = false;
            }

            int n = Math.Min(frames, _count);
            if (channels == 2)
            {
                for (int i = 0; i < n; i++)
                {
                    dest[i * 2] = _buf[_read * 2];
                    dest[i * 2 + 1] = _buf[_read * 2 + 1];
                    if (++_read == _capacity) _read = 0;
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    for (int c = 0; c < channels; c++) dest[i * channels + c] = _buf[_read * channels + c];
                    if (++_read == _capacity) _read = 0;
                }
            }
            _count -= n;
            if (n < frames)
            {
                dest[(n * channels)..].Clear();
                _priming = true;
                _dryPending = true;
                _dryAt = Clock();
                _cleanReads = 0;
                return false;
            }
            int floor = Math.Max(BaseTargetFrames, _floorFrames);
            if (MaxTargetFrames > 0 && TargetFrames > floor && ++_cleanReads >= RelaxAfterReads)
            {
                _cleanReads = 0;
                SetTarget(Math.Max(TargetFrames - _msFrames, floor));
            }
            return true;
        }
    }

    /// <summary>
    /// The source is writing again (<paramref name="arrivedFrames"/> in this write) after the FIFO ran dry: a dropout,
    /// or just the end of a quiet spell or of a short pause of the source.
    /// </summary>
    private void ClassifyDry(int arrivedFrames)
    {
        _dryPending = false;
        if (_dryAt < _graceUntil) return; // the new sender's start-up burst
        long dry = Clock() - _dryAt;
        if (dry > DropoutWindowMs * Stopwatch.Frequency / 1000)
        {
            IdleGaps++;
            return;
        }
        // Dry for clearly longer than what now arrives: the audio was not late, there was none (the remarks above). The
        // capture writes everything that was waiting in one piece, so late delivery brings its backlog with it: 100 ms
        // dry, then 100 ms at once, stays a dropout below; 180 ms dry, then one 10 ms packet, does not.
        long arrived = arrivedFrames * Stopwatch.Frequency / SampleRate;
        if (dry > arrived + SourcePauseMarginMs * Stopwatch.Frequency / 1000)
        {
            IdleGaps++;
            return;
        }
        Underruns++;
        if (MaxTargetFrames <= 0) return;
        // Measured 2026-10-08: relaxing from 16 back to 15 ms dropped out again 10 minutes later. Remember the
        // level that failed and stay at least 2 ms above it for the rest of the session. (The target itself is
        // remembered across runs, see RestoreTarget; the floor is not.)
        _floorFrames = Math.Min(Math.Max(_floorFrames, TargetFrames + 2 * _msFrames), MaxTargetFrames);
        // Grow by what was missing, at least GrowMs. 2026-11-15: Windows delivered nothing for 26 to 100 ms a few times
        // an hour; at 4 ms a dropout the target would have needed dozens of them to cover one. (2026-11-27: only one
        // 10 ms packet came after those holes, so the longer ones were most likely the player pausing between two
        // tracks, which no longer gets here. That is a reading of the log, not a measurement of each hole. Late delivery
        // of that size still grows the target by what was missing.)
        long dryMs = dry * 1000 / Stopwatch.Frequency;
        int grow = (int)Math.Clamp(dryMs, GrowMs, 1000) * _msFrames;
        if (MaxTargetFrames > TargetFrames) SetTarget(Math.Min(TargetFrames + grow, MaxTargetFrames));
    }

    /// <summary>
    /// Sets <see cref="MaxTargetFrames"/> while the FIFO is in use (a new connection's latency): a lower ceiling also
    /// brings the target and the floor down to it, never below <see cref="BaseTargetFrames"/>.
    /// </summary>
    public void SetMaxTarget(int frames)
    {
        lock (_lock)
        {
            MaxTargetFrames = Math.Max(frames, BaseTargetFrames);
            _floorFrames = Math.Min(_floorFrames, MaxTargetFrames);
            if (TargetFrames > MaxTargetFrames) SetTarget(MaxTargetFrames);
        }
    }

    /// <summary>
    /// Starts a connection from a target learned in an earlier run (StreamController.RestoreFifoTarget): never above
    /// <see cref="MaxTargetFrames"/>, and only ever upward, so it never undoes what this run learned or relaxed to. No
    /// floor comes with it: it relaxes like a target that grew here. 2026-11-26: every start began at the base again,
    /// and a virtual sound card that needs 50 ms replayed its dropouts (16 → 20 → 24 ms in a session's first two
    /// minutes) after each update or restart. Called before the connection's sender reads: it primes to the new target.
    /// </summary>
    /// <returns>True when the target was raised.</returns>
    public bool RestoreTarget(int frames)
    {
        lock (_lock)
        {
            frames = Math.Min(frames, MaxTargetFrames); // not adaptive (0): nothing to restore
            if (frames <= TargetFrames) return false;
            SetTarget(frames);
            _cleanReads = 0;
            _priming = true;
            return true;
        }
    }

    /// <summary>
    /// Back to the configured target, with no floor: what was learned belonged to another capture device (see
    /// <see cref="RestoreTarget"/>). Called before a connection's sender reads.
    /// </summary>
    public void ForgetLearnedTarget()
    {
        lock (_lock)
        {
            _floorFrames = 0;
            _cleanReads = 0;
            if (TargetFrames != BaseTargetFrames) SetTarget(BaseTargetFrames);
        }
    }

    private void SetTarget(int frames)
    {
        TargetFrames = frames;
        CapFrames = frames + _capMargin;
    }

    /// <summary>After a stall, throw away anything beyond the target so we don't play stale audio.</summary>
    public void DiscardToTarget()
    {
        lock (_lock)
        {
            if (_count > TargetFrames)
            {
                DroppedFrames += _count - TargetFrames;
                Skip(_count - TargetFrames);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _read = 0;
            _count = 0;
            _priming = true;
            _dryPending = false;
        }
    }

    private void Skip(int frames)
    {
        _read = (_read + frames) % _capacity;
        _count -= frames;
    }
}
