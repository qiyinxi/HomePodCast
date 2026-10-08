using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

// Minimal Core Audio (MMDevice + WASAPI) interop. Only the members we call are declared, but the
// vtable order of every interface must match the SDK headers exactly.

internal enum EDataFlow { Render = 0, Capture = 1, All = 2 }

internal enum ERole { Console = 0, Multimedia = 1, Communications = 2 }

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorCom;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort VarType;
    [FieldOffset(8)] public IntPtr Pointer;
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

// Order checked against NAudio's IAudioEndpointVolume and endpointvolume.h (SDK 10.0.26100);
// members after GetVolumeStepInfo are omitted.
[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out int count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    [PreserveSig] int GetVolumeStepInfo(out uint step, out uint stepCount);
}

// IID and layout from endpointvolume.h (SDK 10.0.26100), same as NAudio: one method after IUnknown.
// A wrong IID registers without error and is then simply never called, so a unit test pins it.
// We implement this one (Windows calls us), so it is a COM-visible interface, not [ComImport].
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown), ComVisible(true)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

/// <summary>AUDIO_VOLUME_NOTIFICATION_DATA without the trailing afChannelVolumes[nChannels].</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct AudioVolumeNotificationData
{
    public Guid EventContext;
    public int Muted;           // BOOL
    public float MasterVolume;  // scalar 0..1
    public uint Channels;
}

internal static class CoreAudio
{
    public static Guid IidAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    public const uint StreamFlagsLoopback = 0x00020000;
    public const uint StreamFlagsEventCallback = 0x00040000;
    public const uint StreamFlagsNoPersist = 0x00080000;
    public const uint StreamFlagsSrcDefaultQuality = 0x08000000;
    public const uint StreamFlagsAutoConvertPcm = 0x80000000;
    public const uint BufferFlagsSilent = 0x2;
    public const int ClsCtxAll = 0x17;
    public const int DeviceInvalidated = unchecked((int)0x88890004);

    public static Guid IidAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IidAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");
    public static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");

    private static PropertyKey _friendlyName = new()
    {
        FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        PropertyId = 14,
    };

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);

    /// <summary>E_NOTFOUND (HRESULT_FROM_WIN32(ERROR_NOT_FOUND)): e.g. GetDefaultAudioEndpoint with no output device at all.</summary>
    public const int NotFound = unchecked((int)0x80070490);

    public static void Check(int hr, string what)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr, IntPtr.Zero);
    }

    public static string FriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out var store) < 0) return "?";
        try
        {
            if (store.GetValue(ref _friendlyName, out var pv) < 0) return "?";
            try
            {
                return pv.VarType == 31 ? Marshal.PtrToStringUni(pv.Pointer) ?? "?" : "?";
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
}

/// <summary>Parsed WAVEFORMATEX / WAVEFORMATEXTENSIBLE.</summary>
internal readonly record struct WaveFormat(int SampleRate, int Channels, int BitsPerSample, int BlockAlign, bool IsFloat)
{
    public static WaveFormat FromPointer(IntPtr p)
    {
        ushort tag = (ushort)Marshal.ReadInt16(p, 0);
        int channels = (ushort)Marshal.ReadInt16(p, 2);
        int rate = Marshal.ReadInt32(p, 4);
        int blockAlign = (ushort)Marshal.ReadInt16(p, 12);
        int bits = (ushort)Marshal.ReadInt16(p, 14);
        bool isFloat = tag == 3;
        if (tag == 0xFFFE)
        {
            var sub = Marshal.PtrToStructure<Guid>(p + 24);
            isFloat = sub == CoreAudio.SubtypeFloat;
        }
        return new WaveFormat(rate, channels, bits, blockAlign, isFloat);
    }
}

// ---- Low-latency shared mode and endpoint lists (mic capture, local monitor) -------------------------
// GUIDs and vtable order checked against the Windows SDK 10.0.26100 headers (Audioclient.idl/.h,
// mmdeviceapi.idl/.h). COM interop does not inherit vtables, so IAudioClient3 repeats every member of
// IAudioClient and IAudioClient2 in order.

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out IMMDevice device);
}

[ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient3
{
    // IAudioClient
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    // IAudioClient2
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(IntPtr properties);
    [PreserveSig] int GetBufferSizeLimits(IntPtr format, int eventDriven, out long minDuration, out long maxDuration);
    // IAudioClient3
    [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames, out uint fundamentalPeriodFrames,
        out uint minPeriodFrames, out uint maxPeriodFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
    [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodFrames, IntPtr format, IntPtr sessionGuid);
}

internal static class CoreAudio3
{
    public static Guid IidAudioClient3 = new("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42");
    public const int DeviceStateActive = 0x1;
    public const int EnginePeriodicityLocked = unchecked((int)0x88890028); // AUDCLNT_E_ENGINE_PERIODICITY_LOCKED

    /// <summary>PKEY_AudioEndpoint_FormFactor (VT_UI4, EndpointFormFactor).</summary>
    public static PropertyKey FormFactorKey = new()
    {
        FormatId = new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"),
        PropertyId = 0,
    };
}
