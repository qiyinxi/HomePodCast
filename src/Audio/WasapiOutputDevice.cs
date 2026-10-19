using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

internal interface ILocalRenderDevice : IDisposable
{
    int Rate { get; }
    int BufferFrames { get; }
    int PeriodFrames { get; }
    double StreamLatencyMs { get; }
    int PaddingFrames { get; }
    RenderPosition Position { get; }
    void Start();
    void WaitReady();
    void Write(ReadOnlySpan<float> stereo);
}

[ComImport, Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClock
{
    [PreserveSig] int GetFrequency(out ulong frequency);
    [PreserveSig] int GetPosition(out ulong position, out ulong qpc100ns);
    [PreserveSig] int GetCharacteristics(out uint characteristics);
}

/// <summary>All COM calls, including disposal, stay on the local sink's render thread. Never opens the default endpoint.</summary>
internal sealed class WasapiOutputDevice : ILocalRenderDevice
{
    private readonly AutoResetEvent _ready = new(false);
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private SharedStream? _stream;
    private IAudioRenderClient? _render;
    private IAudioClock? _clock;
    private ulong _frequency;

    public WasapiOutputDevice(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            _device = AudioEndpoints.Open(_enumerator, EDataFlow.Render, endpointId)
                ?? throw new InvalidOperationException(L.T("找不到所选的输出设备。"));
            _stream = SharedStream.Open(_device, _ready.SafeWaitHandle.DangerousGetHandle());
            var format = _stream.Format;
            if (format.SampleRate <= 0 || format.Channels <= 0 ||
                (format.IsFloat ? format.BitsPerSample != 32 : format.BitsPerSample is not (16 or 24 or 32)))
                throw new InvalidOperationException("Unsupported WASAPI render format");
            CoreAudio.Check(_stream.Client.GetService(ref CoreAudio.IidAudioRenderClient, out var render), "render client");
            _render = (IAudioRenderClient)render;
            var clockId = typeof(IAudioClock).GUID;
            CoreAudio.Check(_stream.Client.GetService(ref clockId, out var clock), "audio clock");
            _clock = (IAudioClock)clock;
            CoreAudio.Check(_clock.GetFrequency(out _frequency), "clock frequency");
            if (_frequency == 0) throw new InvalidOperationException("Invalid WASAPI clock frequency");
        }
        catch { Dispose(); throw; }
    }

    public int Rate => _stream!.Format.SampleRate;
    public int BufferFrames => checked((int)_stream!.BufferFrames);
    public int PeriodFrames => _stream!.PeriodFrames;
    public double StreamLatencyMs => _stream!.StreamLatencyMs;
    public int PaddingFrames
    {
        get { CoreAudio.Check(_stream!.Client.GetCurrentPadding(out uint frames), "render padding"); return checked((int)frames); }
    }
    public RenderPosition Position
    {
        get
        {
            CoreAudio.Check(_clock!.GetPosition(out ulong position, out ulong qpc), "render position");
            return RenderPosition.FromNative(position, _frequency, qpc, Rate);
        }
    }

    public void Start() => CoreAudio.Check(_stream!.Client.Start(), "render start");
    public void WaitReady() => _ready.WaitOne(100);

    public unsafe void Write(ReadOnlySpan<float> stereo)
    {
        int frames = stereo.Length / 2;
        if (stereo.Length % 2 != 0) throw new ArgumentException("Expected complete stereo frames", nameof(stereo));
        if (frames == 0) return;
        CoreAudio.Check(_render!.GetBuffer((uint)frames, out var data), "render buffer");
        try
        {
            var format = _stream!.Format;
            int bytes = format.BitsPerSample / 8;
            for (int i = 0; i < frames; i++)
            {
                byte* frame = (byte*)data + (long)i * format.BlockAlign;
                float left = Finite(stereo[i * 2]), right = Finite(stereo[i * 2 + 1]);
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    float sample = format.Channels == 1 ? (left + right) / 2 : channel == 0 ? left : channel == 1 ? right : 0;
                    byte* target = frame + channel * bytes;
                    if (format.IsFloat) *(float*)target = sample;
                    else if (bytes == 2) *(short*)target = (short)Math.Round(sample * 32767);
                    else if (bytes == 4) *(int*)target = (int)Math.Round(sample * 2147483647.0);
                    else
                    {
                        int value = (int)Math.Round(sample * 8388607);
                        target[0] = (byte)value; target[1] = (byte)(value >> 8); target[2] = (byte)(value >> 16);
                    }
                }
            }
        }
        catch { _render.ReleaseBuffer((uint)frames, CoreAudio.BufferFlagsSilent); throw; }
        CoreAudio.Check(_render.ReleaseBuffer((uint)frames, 0), "release render buffer");
    }

    private static float Finite(float value) => float.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;

    public void Dispose()
    {
        if (_clock != null) { Marshal.ReleaseComObject(_clock); _clock = null; }
        if (_render != null) { Marshal.ReleaseComObject(_render); _render = null; }
        _stream?.Dispose(); _stream = null;
        if (_device != null) { Marshal.ReleaseComObject(_device); _device = null; }
        if (_enumerator != null) { Marshal.ReleaseComObject(_enumerator); _enumerator = null; }
        _ready.Dispose();
    }
}
