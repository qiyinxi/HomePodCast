using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>
/// The wireless cinema's audio (experimental since 1.1, never tested on real hardware): a decoder thread fills a
/// lock-free single-producer/single-consumer ring of up to 0.5 s of 44.1 kHz frames (up to 8 channels); the sender
/// thread (MMCSS "Pro Audio") takes one packet of frames, then reads every speaker's share through the channel
/// matrix and its acoustic delay. The sender never waits for the decoder: before 2026-10-14 both took one lock and
/// the decoder held it while copying half a second of 8-channel audio (review of #22).
/// </summary>
public sealed class CinemaFileSource : IGroupPacketSource
{
    private const int Rate = RtpSender.SampleRate;
    private const int Stride = CinemaChannels.MaxChannels; // floats per frame in the ring and the history
    private const int Capacity = Rate;                      // frames in the ring and in the delay history (1 s)
    private const int MaxBuffered = Rate / 2;               // the decoder stays at most this far ahead; never drops audio
    private const int MaxDelayMs = 500;

    private readonly float[] _ring = new float[Capacity * Stride];
    private readonly float[] _history = new float[Capacity * Stride];
    private readonly CinemaSpeaker[] _speakers;
    private readonly Thread _decoder;
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CinemaMediaReader.Progress _progress = new("starting the decoder thread");
    private readonly float[] _chirp = AcousticAlignment.Chirp();
    private volatile CinemaMatrix? _matrix;
    private volatile bool _stop, _eof, _paused;
    private volatile string? _error, _errorLog;

    // Ring positions in frames since the start: _written only by the decoder thread, _taken only by the sender thread.
    private long _written, _taken;

    // Sender thread only.
    private int _historyWrite;
    private int[] _delays;
    private long _activeRequest;
    private int _pulseCursor;
    private long _packetTicks;
    private int _tailFrames, _longestDelayFrames;

    // Calibration: the calibrating thread writes the request, the sender thread only reads it (no lost update).
    private long _request;        // 0: none; else (generation << 8) | (speaker + 1)
    private long _generation;
    private long _pulseStartedTicks;
    private int[]? _pendingDelays;

    /// <summary>The translated reason playback stopped (null while it plays); <see cref="ErrorLog"/> says it for the log.</summary>
    public string? Error => _error;
    public string? ErrorLog => _errorLog;
    public int Channels { get; private set; }
    public int SourceRate { get; private set; }
    public uint Mask { get; private set; }
    public int BufferedFrames => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _taken));
    public bool Paused { get => _paused; set => _paused = value; }
    /// <summary>The file is over and every speaker's delayed tail has been sent.</summary>
    public bool Finished => _eof && BufferedFrames == 0 && Volatile.Read(ref _tailFrames) >= Volatile.Read(ref _longestDelayFrames);
    public double PositionSeconds => Volatile.Read(ref _taken) / (double)Rate;
    public bool Calibrating => Volatile.Read(ref _request) != 0;
    public long PulseStartedTicks => Volatile.Read(ref _pulseStartedTicks);

    /// <summary>What each speaker plays, for the log (after <see cref="WaitOpened"/>).</summary>
    internal string? Routing => _matrix?.Describe();

    /// <exception cref="CinemaException">The speaker layout is not usable.</exception>
    public CinemaFileSource(string path, IReadOnlyList<CinemaSpeaker> speakers)
        : this(path, speakers, (file, step) => new CinemaMediaReader(file, step)) { }

    /// <param name="open">Opens the decoder on the decoder thread (a test holds it there to be a slow open).</param>
    internal CinemaFileSource(string path, IReadOnlyList<CinemaSpeaker> speakers, Func<string, Action<string>, CinemaMediaReader> open)
    {
        CinemaMatrix.Validate(speakers);
        _speakers = speakers.Select(s => new CinemaSpeaker { Role = s.Role, DeviceId = s.DeviceId, Name = s.Name, DelayMs = s.DelayMs }).ToArray();
        _delays = _speakers.Select(s => s.DelayMs).ToArray();
        _longestDelayFrames = _delays.Max() * Rate / 1000;
        _decoder = new Thread(() => Decode(path, open)) { IsBackground = true, Name = "Cinema file decoder" };
        _decoder.SetApartmentState(ApartmentState.MTA); _decoder.Start();
    }

    /// <summary>
    /// Completes once the decoder has opened the file and its layout fits the speakers (true), or has given up (false,
    /// <see cref="Error"/> says why). Every waiter sees it, whether it subscribed before or after; awaiting it holds no
    /// thread (before 2026-10-17 the window kept a pool thread blocked in <see cref="WaitOpened"/> for up to 10 s).
    /// </summary>
    public Task<bool> Opened => _opened.Task;

    /// <summary>Where the decoder is and since when, e.g. "opening the file: MFCreateSourceReaderFromURL for 10.0 s".</summary>
    public string Progress => _progress.ToString();

    /// <summary>The file opened and its layout fits the speakers; false with <see cref="Error"/> set otherwise, or on timeout
    /// with <see cref="Progress"/> saying where the decoder waits.</summary>
    public bool WaitOpened(TimeSpan timeout) => _opened.Task.Wait(timeout) && _opened.Task.Result && _error == null;

    private void Fail(CinemaException ex)
    {
        _errorLog = ex.LogText;
        _error = ex.Message;
    }

    private void Decode(string path, Func<string, Action<string>, CinemaMediaReader> open)
    {
        try
        {
            _progress.Step("opening the file");
            using var reader = open(path, step => _progress.Step("opening the file: " + step));
            Channels = reader.Channels; SourceRate = reader.Rate; Mask = reader.Mask;
            _matrix = new CinemaMatrix(reader.Channels, reader.Mask, _speakers);
            reader.MaskChanged += mask =>
            {
                Mask = mask;
                _matrix = new CinemaMatrix(Channels, mask, _speakers); // what is buffered (≤ 0.5 s) plays with the new one
            };
            _progress.Step("decoding");
            _opened.TrySetResult(true);

            int channels = reader.Channels;
            // Stereo resamplers over channel pairs (the last one padded for an odd count); none at 44.1 kHz.
            var resamplers = reader.Rate == Rate ? null
                : Enumerable.Range(0, (channels + 1) / 2).Select(_ => new Resampler(reader.Rate, Rate)).ToArray();
            var pair = new List<float>();
            var output = resamplers?.Select(_ => new List<float>()).ToArray();
            var interleaved = Array.Empty<float>();
            while (!_stop)
            {
                var input = reader.Read();
                if (input == null) break;
                int inFrames = input.Length / channels;
                if (resamplers == null)
                {
                    Push(input, inFrames);
                    continue;
                }
                for (int p = 0; p < resamplers.Length; p++)
                {
                    pair.Clear(); output![p].Clear();
                    int a = p * 2, b = a + 1;
                    for (int f = 0; f < inFrames; f++)
                    {
                        pair.Add(input[f * channels + a]);
                        pair.Add(b < channels ? input[f * channels + b] : 0);
                    }
                    resamplers[p].Process(CollectionsMarshal.AsSpan(pair), output[p]);
                }
                int frames = output![0].Count / 2;
                if (output.Any(o => o.Count != frames * 2)) throw new InvalidOperationException("channel resampling clocks diverged");
                if (interleaved.Length < frames * channels) interleaved = new float[frames * channels * 2];
                for (int f = 0; f < frames; f++)
                    for (int c = 0; c < channels; c++)
                        interleaved[f * channels + c] = output[c / 2][f * 2 + (c & 1)];
                Push(interleaved, frames);
            }
            // The sinc's final half-kernel is not fabricated; EOF leaves its sub-ms tail unplayed.
        }
        catch (CinemaException ex) { Fail(ex); }
        catch (Exception ex) { Fail(new CinemaException(L.T("播放中解码出错，已停止。"), $"{ex.GetType().Name}: {ex.Message}", ex)); }
        finally
        {
            _eof = true;
            _progress.Step(_error != null ? "failed: " + _errorLog : _stop ? "stopped" : "at the end of the file");
            _opened.TrySetResult(false); // a no-op once it opened
        }
    }

    /// <summary>Decoder thread: interleaved frames into the ring, waiting (never dropping) while it holds <see cref="MaxBuffered"/>.</summary>
    private void Push(float[] interleaved, int frames)
    {
        int channels = Channels;
        for (int at = 0; at < frames && !_stop;)
        {
            long written = _written;
            int room = (int)Math.Min(frames - at, MaxBuffered - (written - Volatile.Read(ref _taken)));
            if (room <= 0)
            {
                Thread.Sleep(5);
                continue;
            }
            for (int f = 0; f < room; f++)
                interleaved.AsSpan((at + f) * channels, channels).CopyTo(_ring.AsSpan((int)((written + f) % Capacity) * Stride, channels));
            Volatile.Write(ref _written, written + room); // publishes the frames to the sender
            at += room;
        }
    }

    /// <summary>Sender thread: take one packet of frames into the delay history (silence when paused or measuring).</summary>
    public bool ReadPacket(long sourceTicks)
    {
        _packetTicks = sourceTicks;
        if (Interlocked.Exchange(ref _pendingDelays, null) is { } offsets)
        {
            _delays = offsets;
            Volatile.Write(ref _longestDelayFrames, offsets.Max() * Rate / 1000);
            Array.Clear(_history);
        }
        long request = Volatile.Read(ref _request);
        if (request != _activeRequest)
        {
            _activeRequest = request;
            _pulseCursor = 0; // a new pulse (or the end of measuring)
        }
        if (_paused || request != 0) return true; // the file position is kept while paused or measuring

        long available = Volatile.Read(ref _written) - _taken;
        int take = (int)Math.Min(RtpSender.FramesPerPacket, available);
        for (int f = 0; f < RtpSender.FramesPerPacket; f++)
        {
            var dest = _history.AsSpan((_historyWrite + f) % Capacity * Stride, Stride);
            if (f < take) _ring.AsSpan((int)((_taken + f) % Capacity) * Stride, Stride).CopyTo(dest);
            else dest.Clear();
        }
        if (_eof && take < RtpSender.FramesPerPacket) Volatile.Write(ref _tailFrames, _tailFrames + RtpSender.FramesPerPacket - take);
        Volatile.Write(ref _taken, _taken + take); // hands the slots back to the decoder
        _historyWrite = (_historyWrite + RtpSender.FramesPerPacket) % Capacity;
        return take == RtpSender.FramesPerPacket;
    }

    /// <summary>Sender thread: speaker <paramref name="index"/>'s share of the packet just read (the same on both channels).</summary>
    public void ReadTarget(int index, Span<float> stereo)
    {
        if (_activeRequest != 0)
        {
            stereo.Clear();
            if ((int)(_activeRequest & 0xff) - 1 != index) return;
            int cursor = _pulseCursor;
            if (cursor == 0) Volatile.Write(ref _pulseStartedTicks, _packetTicks);
            for (int f = 0; f < stereo.Length / 2; f++)
                if (cursor + f < _chirp.Length) stereo[f * 2] = stereo[f * 2 + 1] = _chirp[cursor + f];
            _pulseCursor = Math.Min(_chirp.Length, cursor + stereo.Length / 2);
            return;
        }
        var matrix = _matrix;
        if (_paused || matrix == null) { stereo.Clear(); return; }
        int delay = _delays[index] * Rate / 1000;
        int first = (_historyWrite - RtpSender.FramesPerPacket - delay + 2 * Capacity) % Capacity;
        for (int f = 0; f < stereo.Length / 2; f++)
        {
            var (left, right) = matrix.StereoSample(index, _history.AsSpan((first + f) % Capacity * Stride, matrix.Channels));
            stereo[f * 2] = left; stereo[f * 2 + 1] = right;
        }
    }

    /// <summary>Sender thread, after a stall: skip what was due meanwhile.</summary>
    public void SkipPackets(int packets)
    {
        if (_activeRequest != 0 || _paused) return;
        long available = Volatile.Read(ref _written) - _taken;
        int frames = (int)Math.Min((long)packets * RtpSender.FramesPerPacket, available);
        Volatile.Write(ref _taken, _taken + frames);
        Array.Clear(_history); // no stale delayed audio after a sender stall
    }

    /// <summary>The next packets carry the test chirp on <paramref name="speaker"/> only; the file waits meanwhile.</summary>
    public void BeginPulse(int speaker)
    {
        if (speaker < 0 || speaker >= _speakers.Length) throw new ArgumentOutOfRangeException(nameof(speaker));
        Volatile.Write(ref _pulseStartedTicks, 0);
        long generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _request, (generation << 8) | (uint)(speaker + 1));
    }

    /// <summary>Back to the file. Whenever it is called (even while the sender starts a pulse), the next packet plays the file.</summary>
    public void EndCalibration() => Volatile.Write(ref _request, 0);

    public void ApplyDelays(IReadOnlyList<int> delays)
    {
        if (delays.Count != _speakers.Length || delays.Any(d => d is < 0 or > MaxDelayMs)) throw new ArgumentException("invalid acoustic delay set");
        Volatile.Write(ref _pendingDelays, delays.ToArray());
    }

    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _decoder) _decoder.Join(3000);
    }
}
