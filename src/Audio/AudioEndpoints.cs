using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>EndpointFormFactor from mmdeviceapi.h.</summary>
public enum EndpointFormFactor
{
    RemoteNetworkDevice, Speakers, LineLevel, Headphones, Microphone, Headset, Handset,
    UnknownDigitalPassthrough, Spdif, DigitalAudioDisplayDevice, Unknown,
}

public sealed record AudioEndpoint(string Id, string Name, EndpointFormFactor FormFactor)
{
    /// <summary>Headphones or a headset: safe to monitor a microphone on without feedback.</summary>
    public bool IsHeadphones => FormFactor is EndpointFormFactor.Headphones or EndpointFormFactor.Headset;
}

/// <summary>Active input/output endpoints, and opening one by id (null = the default device).</summary>
public static class AudioEndpoints
{
    public static List<AudioEndpoint> Inputs() => List(EDataFlow.Capture);
    public static List<AudioEndpoint> Outputs() => List(EDataFlow.Render);

    public static AudioEndpoint? DefaultInput() => Default(EDataFlow.Capture);
    public static AudioEndpoint? DefaultOutput() => Default(EDataFlow.Render);

    private static List<AudioEndpoint> List(EDataFlow flow)
    {
        var result = new List<AudioEndpoint>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDeviceCollection? collection = null;
        try
        {
            if (enumerator.EnumAudioEndpoints(flow, CoreAudio3.DeviceStateActive, out var raw) < 0 || raw == IntPtr.Zero) return result;
            try { collection = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(raw); }
            finally { Marshal.Release(raw); }
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) < 0) continue;
                try { if (Describe(device) is { } e) result.Add(e); }
                finally { Marshal.ReleaseComObject(device); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"endpoints: {ex.Message}");
        }
        finally
        {
            if (collection != null) Marshal.ReleaseComObject(collection);
            Marshal.ReleaseComObject(enumerator);
        }
        return result.OrderBy(e => e.Name, StringComparer.CurrentCulture).ToList();
    }

    private static AudioEndpoint? Default(EDataFlow flow)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device) < 0) return null;
            try { return Describe(device); }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    private static AudioEndpoint? Describe(IMMDevice device)
    {
        if (device.GetId(out var id) < 0 || id == null) return null;
        return new AudioEndpoint(id, CoreAudio.FriendlyName(device), FormFactor(device));
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    private static EndpointFormFactor FormFactor(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0) return EndpointFormFactor.Unknown;
        try
        {
            if (store.GetValue(ref CoreAudio3.FormFactorKey, out var pv) < 0) return EndpointFormFactor.Unknown;
            try
            {
                const ushort vtUi4 = 19;
                if (pv.VarType != vtUi4) return EndpointFormFactor.Unknown;
                uint v = (uint)(pv.Pointer.ToInt64() & 0xFFFFFFFF);
                return v <= (uint)EndpointFormFactor.Unknown ? (EndpointFormFactor)v : EndpointFormFactor.Unknown;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>The device with this id (null/empty = default for the flow); null if it is gone.</summary>
    internal static IMMDevice? Open(IMMDeviceEnumerator enumerator, EDataFlow flow, string? id)
    {
        IMMDevice? device;
        int hr = string.IsNullOrEmpty(id)
            ? enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out device)
            : enumerator.GetDevice(id, out device);
        if (hr < 0 || device == null) return null;
        if (device.GetState(out int state) < 0 || state != CoreAudio3.DeviceStateActive)
        {
            Marshal.ReleaseComObject(device);
            return null;
        }
        return device;
    }

    /// <summary>Id of the current default device for the flow, or null.</summary>
    internal static string? DefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        if (enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device) < 0) return null;
        try { return device.GetId(out var id) >= 0 ? id : null; }
        finally { Marshal.ReleaseComObject(device); }
    }
}

/// <summary>
/// A shared-mode, event-driven WASAPI stream at the engine's mix format with the smallest period on
/// offer: IAudioClient3.InitializeSharedAudioStream at the minimum period when the driver supports it,
/// otherwise the classic IAudioClient.Initialize (one engine period, usually 10 ms).
/// </summary>
internal sealed class SharedStream : IDisposable
{
    public IAudioClient Client { get; }
    public WaveFormat Format { get; }
    public int PeriodFrames { get; }
    public uint BufferFrames { get; }
    public bool LowLatency { get; }
    public double StreamLatencyMs { get; }
    public double PeriodMs => PeriodFrames * 1000.0 / Format.SampleRate;
    public string Mode => LowLatency ? "IAudioClient3" : "IAudioClient";

    private IntPtr _mix;

    private SharedStream(IAudioClient client, IntPtr mix, int periodFrames, bool lowLatency)
    {
        Client = client;
        _mix = mix;
        Format = WaveFormat.FromPointer(mix);
        PeriodFrames = periodFrames;
        LowLatency = lowLatency;
        CoreAudio.Check(client.GetBufferSize(out uint buffer), "buffer size");
        BufferFrames = buffer;
        StreamLatencyMs = client.GetStreamLatency(out long latency) >= 0 ? latency / 10_000.0 : 0;
    }

    public static SharedStream Open(IMMDevice device, IntPtr eventHandle)
    {
        var stream = TryLowLatency(device) ?? OpenClassic(device);
        try
        {
            CoreAudio.Check(stream.Client.SetEventHandle(eventHandle), "event");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static SharedStream? TryLowLatency(IMMDevice device)
    {
        IAudioClient3? c3 = null;
        IntPtr mix = IntPtr.Zero;
        try
        {
            if (device.Activate(ref CoreAudio3.IidAudioClient3, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) < 0) return null;
            c3 = (IAudioClient3)obj;
            if (c3.GetMixFormat(out mix) < 0) return null;
            if (c3.GetSharedModeEnginePeriod(mix, out _, out _, out uint minPeriod, out _) < 0) return null;
            uint period = minPeriod;
            int hr = c3.InitializeSharedAudioStream(CoreAudio.StreamFlagsEventCallback, period, mix, IntPtr.Zero);
            if (hr == CoreAudio3.EnginePeriodicityLocked && c3.GetCurrentSharedModeEnginePeriod(out var current, out uint currentPeriod) >= 0)
            {
                // Another app already runs the engine at a fixed small period: join it.
                Marshal.FreeCoTaskMem(current);
                period = currentPeriod;
                hr = c3.InitializeSharedAudioStream(CoreAudio.StreamFlagsEventCallback, period, mix, IntPtr.Zero);
            }
            if (hr < 0)
            {
                Log.Info($"IAudioClient3 init failed 0x{hr:X8}, using the classic shared stream");
                return null;
            }
            var stream = new SharedStream((IAudioClient)c3, mix, (int)period, lowLatency: true);
            mix = IntPtr.Zero;
            c3 = null;
            return stream;
        }
        catch (Exception ex)
        {
            Log.Info($"IAudioClient3 unavailable ({ex.Message}), using the classic shared stream");
            return null;
        }
        finally
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            if (c3 != null) Marshal.ReleaseComObject(c3);
        }
    }

    private static SharedStream OpenClassic(IMMDevice device)
    {
        CoreAudio.Check(device.Activate(ref CoreAudio.IidAudioClient, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj), "activate");
        var client = (IAudioClient)obj;
        IntPtr mix = IntPtr.Zero;
        try
        {
            CoreAudio.Check(client.GetMixFormat(out mix), "mix format");
            CoreAudio.Check(client.GetDevicePeriod(out long defaultPeriod, out _), "device period");
            CoreAudio.Check(client.Initialize(0, CoreAudio.StreamFlagsEventCallback | CoreAudio.StreamFlagsNoPersist,
                defaultPeriod, 0, mix, IntPtr.Zero), "initialize");
            int rate = WaveFormat.FromPointer(mix).SampleRate;
            var stream = new SharedStream(client, mix, (int)Math.Round(defaultPeriod * rate / 10_000_000.0), lowLatency: false);
            mix = IntPtr.Zero;
            return stream;
        }
        catch
        {
            if (mix != IntPtr.Zero) Marshal.FreeCoTaskMem(mix);
            Marshal.ReleaseComObject(client);
            throw;
        }
    }

    public void Dispose()
    {
        try { Client.Stop(); } catch { }
        if (_mix != IntPtr.Zero) Marshal.FreeCoTaskMem(_mix);
        _mix = IntPtr.Zero;
        Marshal.ReleaseComObject(Client);
    }
}
