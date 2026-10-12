using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>Bounded native decoder → one 8-channel ring → per-speaker matrix and acoustic delay → group RTP.</summary>
public sealed class CinemaFileSource : IGroupPacketSource
{
    private const int Rate = 44100, Capacity = Rate;
    private readonly object _queueLock = new();
    private readonly float[] _queue = new float[Capacity * 8];
    private readonly float[] _history = new float[Capacity * 8];
    private readonly CinemaSpeaker[] _speakers;
    private readonly Thread _decoder;
    private readonly ManualResetEventSlim _opened = new();
    private readonly float[] _chirp = AcousticAlignment.Chirp();
    private CinemaMatrix? _matrix;
    private int _read, _count, _historyWrite;
    private volatile bool _stop, _eof, _paused;
    private int _calibration = -1, _pulseCursor = int.MaxValue;
    private int _pendingPulse = -1;
    private volatile int[] _delays;
    private int[]? _pendingDelays;
    private long _position;
    private int _tailFrames;
    private long _packetTicks, _pulseStartedTicks;
    public string? Error { get; private set; }
    public int Channels { get; private set; }
    public int BufferedFrames => Volatile.Read(ref _count);
    public bool Paused { get => _paused; set => _paused = value; }
    public bool Finished => _eof && Volatile.Read(ref _count) == 0 && _tailFrames >= _delays.Max() * Rate / 1000;
    public double PositionSeconds => Volatile.Read(ref _position) / (double)Rate;
    public bool Calibrating => Volatile.Read(ref _calibration) >= 0 || Volatile.Read(ref _pendingPulse) >= 0;
    public long PulseStartedTicks => Volatile.Read(ref _pulseStartedTicks);

    public CinemaFileSource(string path, IReadOnlyList<CinemaSpeaker> speakers)
    {
        CinemaMatrix.Validate(speakers);
        _speakers = speakers.Select(s => new CinemaSpeaker { Role = s.Role, DeviceId = s.DeviceId, DelayMs = s.DelayMs }).ToArray();
        _delays = _speakers.Select(s => s.DelayMs).ToArray();
        _decoder = new Thread(() => Decode(path)) { IsBackground = true, Name = "Cinema file decoder" };
        _decoder.SetApartmentState(ApartmentState.MTA); _decoder.Start();
    }
    public bool WaitOpened(TimeSpan timeout) => _opened.Wait(timeout) && Error == null && _matrix != null;
    private void Decode(string path)
    {
        try
        {
            using var reader = new CinemaMediaReader(path);
            Channels = reader.Channels; _matrix = new CinemaMatrix(reader.Channels, reader.Mask, _speakers); _opened.Set();
            var resamplers = Enumerable.Range(0, reader.Channels / 2).Select(_ => new Resampler(reader.Rate, Rate)).ToArray();
            var pair = new List<float>(); var output = resamplers.Select(_ => new List<float>()).ToArray();
            while (!_stop)
            {
                var input = reader.Read();
                if (input == null) break;
                for (int p = 0; p < resamplers.Length; p++)
                {
                    pair.Clear(); output[p].Clear();
                    for (int f = 0; f < input.Length / reader.Channels; f++)
                    { pair.Add(input[f * reader.Channels + p * 2]); pair.Add(input[f * reader.Channels + p * 2 + 1]); }
                    resamplers[p].Process(CollectionsMarshal.AsSpan(pair), output[p]);
                }
                int frames = output[0].Count / 2;
                if (output.Any(p => p.Count != frames * 2)) throw new InvalidDataException("Channel resampling clocks diverged.");
                for (int at = 0; at < frames && !_stop;)
                {
                    int written = 0;
                    lock (_queueLock)
                    {
                        written = Math.Min(frames - at, Rate / 2 - _count); // at most half a second, backpressure never drops audio
                        for (int f = 0; f < written; f++)
                        {
                            int index = (_read + _count + f) % Capacity;
                            for (int p = 0; p < output.Length; p++)
                            { _queue[index * 8 + p * 2] = output[p][(at + f) * 2]; _queue[index * 8 + p * 2 + 1] = output[p][(at + f) * 2 + 1]; }
                        }
                        _count += written;
                    }
                    at += written;
                    if (written == 0) Thread.Sleep(5);
                }
            }
            // The sinc's final half-kernel is not fabricated; EOF leaves its sub-ms tail unplayed.
            _eof = true;
        }
        catch (Exception ex) { Error = ex.Message; _eof = true; }
        finally { _opened.Set(); }
    }
    public bool ReadPacket(long sourceTicks)
    {
        _packetTicks = sourceTicks;
        if (Interlocked.Exchange(ref _pendingDelays, null) is { } offsets) { _delays = offsets; Array.Clear(_history); }
        int pulse = Interlocked.Exchange(ref _pendingPulse, -1);
        if (pulse >= 0) { _pulseCursor = 0; Volatile.Write(ref _calibration, pulse); }
        if (_paused || Calibrating) return true; // preserve exact file position while paused/measuring
        lock (_queueLock)
        {
            for (int f = 0; f < RtpSender.FramesPerPacket; f++)
            {
                int history = (_historyWrite + f) % Capacity;
                var dest = _history.AsSpan(history * 8, 8);
                if (f < _count) _queue.AsSpan(((_read + f) % Capacity) * 8, 8).CopyTo(dest);
                else dest.Clear();
            }
            int take = Math.Min(RtpSender.FramesPerPacket, _count);
            if (_eof) _tailFrames += RtpSender.FramesPerPacket - take;
            _read = (_read + take) % Capacity; _count -= take; _position += take;
            _historyWrite = (_historyWrite + RtpSender.FramesPerPacket) % Capacity;
            return take == RtpSender.FramesPerPacket;
        }
    }
    public void ReadTarget(int index, Span<float> stereo)
    {
        int target = Volatile.Read(ref _calibration);
        if (target >= 0)
        {
            stereo.Clear();
            if (target != index) return;
            int cursor = Volatile.Read(ref _pulseCursor);
            if (cursor == 0) Volatile.Write(ref _pulseStartedTicks, _packetTicks);
            for (int f = 0; f < stereo.Length / 2; f++)
                if (cursor + f < _chirp.Length) stereo[f * 2] = stereo[f * 2 + 1] = _chirp[cursor + f];
            Volatile.Write(ref _pulseCursor, Math.Min(_chirp.Length, cursor + stereo.Length / 2));
            return;
        }
        if (_paused || _matrix == null) { stereo.Clear(); return; }
        int delay = _delays[index] * Rate / 1000;
        int first = (_historyWrite - RtpSender.FramesPerPacket - delay + Capacity) % Capacity;
        for (int f = 0; f < stereo.Length / 2; f++)
        {
            float value = _matrix.Sample(index, _history.AsSpan(((first + f) % Capacity) * 8, Channels));
            stereo[f * 2] = stereo[f * 2 + 1] = value;
        }
    }
    public void SkipPackets(int packets)
    {
        if (Calibrating || _paused) return;
        lock (_queueLock)
        {
            int frames = (int)Math.Min((long)packets * RtpSender.FramesPerPacket, _count);
            _read = (_read + frames) % Capacity; _count -= frames; _position += frames;
            Array.Clear(_history); // no stale delayed audio after a sender stall
        }
    }
    public void BeginPulse(int speaker)
    {
        if (speaker < 0 || speaker >= _speakers.Length) throw new ArgumentOutOfRangeException(nameof(speaker));
        Volatile.Write(ref _pulseStartedTicks, 0);
        Volatile.Write(ref _pendingPulse, speaker);
    }
    public void EndCalibration() { Volatile.Write(ref _pendingPulse, -1); Volatile.Write(ref _calibration, -1); }
    public void ApplyDelays(IReadOnlyList<int> delays)
    {
        if (delays.Count != _speakers.Length || delays.Any(d => d is < 0 or > 500)) throw new ArgumentException("Invalid acoustic delay set.");
        Volatile.Write(ref _pendingDelays, delays.ToArray());
    }
    public void Dispose()
    {
        _stop = true;
        if (Thread.CurrentThread != _decoder) _decoder.Join(3000);
    }
}
