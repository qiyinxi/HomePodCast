using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

// Process loopback (Windows 10 2004 / build 19041+): an IAudioClient activated on the virtual device
// VAD\Process_Loopback captures one process tree (INCLUDE) or everything except one tree (EXCLUDE).
// Declarations checked against mmdeviceapi.h, audioclientactivationparams.idl and objidlbase.h from
// Windows SDK 10.0.26100.0, and against NAudio's WasapiCapture.CreateForProcessCaptureAsync.

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

/// <summary>Marker: the completion handler is called on an arbitrary MTA thread, so it must be agile.</summary>
[ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject;

internal static class ProcessLoopback
{
    public const string VirtualDevice = @"VAD\Process_Loopback";
    public const int MinimumBuild = 19041;
    private const ushort VtBlob = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ModeInclude = 0, ModeExclude = 1;

    /// <summary>Process loopback needs Windows 10 2004 (build 19041) or later.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    /// <summary>
    /// Activate an IAudioClient that captures <paramref name="pid"/>'s process tree (include) or every
    /// process except that tree (exclude). Blocks until activation completes. Call from an MTA thread.
    /// </summary>
    public static IAudioClient Activate(uint pid, bool includeTree, int timeoutMs = 3000)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("process loopback needs Windows 10 2004+");

        // AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType; { TargetProcessId; ProcessLoopbackMode } } = 12 bytes,
        // passed as a PROPVARIANT of type VT_BLOB { cbSize at 8, pBlobData at 8 + pointer size }.
        IntPtr parameters = Marshal.AllocHGlobal(12);
        IntPtr variant = Marshal.AllocHGlobal(24);
        bool completed = false;
        try
        {
            Marshal.WriteInt32(parameters, 0, ActivationTypeProcessLoopback);
            Marshal.WriteInt32(parameters, 4, unchecked((int)pid));
            Marshal.WriteInt32(parameters, 8, includeTree ? ModeInclude : ModeExclude);
            for (int i = 0; i < 24; i += 4) Marshal.WriteInt32(variant, i, 0);
            Marshal.WriteInt16(variant, 0, unchecked((short)VtBlob));
            Marshal.WriteInt32(variant, 8, 12);
            Marshal.WriteIntPtr(variant, 8 + IntPtr.Size, parameters);

            var handler = new CompletionHandler();
            var iid = CoreAudio.IidAudioClient;
            CoreAudio.Check(ActivateAudioInterfaceAsync(VirtualDevice, ref iid, variant, handler, out var operation), "activate process loopback");
            try
            {
                if (!handler.Done.Wait(timeoutMs)) throw new TimeoutException("process loopback activation timed out");
                completed = true;
                CoreAudio.Check(operation.GetActivateResult(out int hr, out var unknown), "activate result");
                CoreAudio.Check(hr, "activate process loopback");
                return (IAudioClient)(unknown ?? throw new COMException("no audio client", unchecked((int)0x80004003)));
            }
            finally
            {
                Marshal.ReleaseComObject(operation);
            }
        }
        finally
        {
            // After a timeout the activation may still read the parameters: leak 36 bytes rather than free them.
            if (completed)
            {
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }
    }

    /// <summary>WAVEFORMATEX for interleaved 32-bit float stereo; free with Marshal.FreeHGlobal.</summary>
    public static IntPtr FloatStereoFormat(int sampleRate)
    {
        IntPtr p = Marshal.AllocHGlobal(18);
        Marshal.WriteInt16(p, 0, 3);                    // WAVE_FORMAT_IEEE_FLOAT
        Marshal.WriteInt16(p, 2, 2);                    // channels
        Marshal.WriteInt32(p, 4, sampleRate);
        Marshal.WriteInt32(p, 8, sampleRate * 8);       // bytes per second
        Marshal.WriteInt16(p, 12, 8);                   // block align
        Marshal.WriteInt16(p, 14, 32);                  // bits per sample
        Marshal.WriteInt16(p, 16, 0);                   // cbSize
        return p;
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Done.Set();
            return 0;
        }
    }
}

/// <summary>
/// One WASAPI loopback capture stream (the whole endpoint, or one process tree), drained as float
/// stereo at its own rate. Create, drain and dispose on the same (MTA) capture thread.
/// </summary>
internal sealed class LoopbackStream : IDisposable
{
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private float[] _stereo = new float[4096];

    public WaveFormat Format { get; }
    public string Label { get; }

    /// <summary>Called for every packet with (frames, flags, QPC position in 100 ns units).</summary>
    public Action<uint, uint, ulong>? PacketObserver { get; set; }

    private LoopbackStream(IAudioClient client, WaveFormat format, string label)
    {
        _client = client;
        Format = format;
        Label = label;
    }

    /// <summary>Loopback of the whole endpoint in its mix format (what the existing capture path uses).</summary>
    public static LoopbackStream OpenEndpoint(IMMDevice device, IntPtr readyEvent, long bufferDuration)
    {
        CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
        var client = (IAudioClient)obj;
        IntPtr mix = IntPtr.Zero;
        try
        {
            CoreAudio.Check(client.GetMixFormat(out mix), "mix format");
            var fmt = WaveFormat.FromPointer(mix);
            CoreAudio.Check(client.Initialize(0,
                CoreAudio.StreamFlagsLoopback | CoreAudio.StreamFlagsEventCallback | CoreAudio.StreamFlagsNoPersist,
                bufferDuration, 0, mix, IntPtr.Zero), "initialize");
            var stream = new LoopbackStream(client, fmt, "endpoint");
            stream.Finish(readyEvent);
            return stream;
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
        }
    }

    /// <summary>Loopback of one process tree (include) or of everything but it (exclude), as float stereo.</summary>
    public static LoopbackStream OpenProcess(uint pid, bool includeTree, int sampleRate, IntPtr readyEvent, long bufferDuration,
        bool autoConvert = true)
    {
        var client = ProcessLoopback.Activate(pid, includeTree);
        IntPtr fmtPtr = ProcessLoopback.FloatStereoFormat(sampleRate);
        try
        {
            uint flags = CoreAudio.StreamFlagsLoopback | CoreAudio.StreamFlagsEventCallback;
            if (autoConvert) flags |= CoreAudio.StreamFlagsAutoConvertPcm | CoreAudio.StreamFlagsSrcDefaultQuality;
            CoreAudio.Check(client.Initialize(0, flags, bufferDuration, 0, fmtPtr, IntPtr.Zero), "initialize");
            var stream = new LoopbackStream(client, new WaveFormat(sampleRate, 2, 32, 8, true),
                $"{(includeTree ? "include" : "exclude")} {pid}");
            stream.Finish(readyEvent);
            return stream;
        }
        catch
        {
            Marshal.ReleaseComObject(client);
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(fmtPtr);
        }
    }

    private void Finish(IntPtr readyEvent)
    {
        CoreAudio.Check(_client!.SetEventHandle(readyEvent), "event");
        CoreAudio.Check(_client.GetService(ref CoreAudio.IidAudioCaptureClient, out var svc), "capture client");
        _capture = (IAudioCaptureClient)svc;
    }

    public void Start() => CoreAudio.Check(_client!.Start(), "start");

    /// <summary>
    /// Append every pending packet to <paramref name="dest"/> as interleaved float stereo at
    /// <see cref="Format"/>'s rate. Returns false once the device is gone (re-open to continue).
    /// </summary>
    public bool Drain(List<float> dest)
    {
        var capture = _capture!;
        while (true)
        {
            int hr = capture.GetNextPacketSize(out uint pending);
            if (hr == CoreAudio.DeviceInvalidated) return false;
            CoreAudio.Check(hr, "packet size");
            if (pending == 0) return true;

            hr = capture.GetBuffer(out var data, out uint frames, out uint flags, out _, out ulong qpc);
            if (hr == CoreAudio.DeviceInvalidated) return false;
            CoreAudio.Check(hr, "get buffer");
            PacketObserver?.Invoke(frames, flags, qpc);

            if (_stereo.Length < frames * 2) _stereo = new float[frames * 2];
            var span = _stereo.AsSpan(0, (int)frames * 2);
            if ((flags & CoreAudio.BufferFlagsSilent) != 0) span.Clear();
            else LoopbackCapture.ToStereo(data, (int)frames, Format, span);
            capture.ReleaseBuffer(frames);

            foreach (var s in span) dest.Add(s);
        }
    }

    public void Dispose()
    {
        try { _client?.Stop(); } catch { }
        if (_capture != null) Marshal.ReleaseComObject(_capture);
        if (_client != null) Marshal.ReleaseComObject(_client);
        _capture = null;
        _client = null;
    }
}
