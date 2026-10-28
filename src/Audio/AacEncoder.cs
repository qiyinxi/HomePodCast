using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomePodCast.Audio;

internal interface IAacEncoder : IDisposable
{
    /// <param name="pcm">1024 interleaved frames of the encoder's channel count.</param>
    IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> pcm);
}

/// <summary>
/// Windows' built-in AAC-LC Media Foundation transform. Own it on one MTA thread, including Dispose.
/// Raw access units: 44.1 kHz stereo for the third-party path, 48 kHz 5.1 for a HomePod (experimental since 1.4); no
/// bundled executable, codec DLL or registration.
/// ABI and attribute identifiers are from the Windows SDK mfobjects.h / mftransform.h / mfapi.h.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AacEncoder : IAacEncoder
{
    private nint _transform;
    private bool _com, _mf;
    private long _frames;
    private readonly int _channels, _rate;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private const int NeedInput = unchecked((int)0xc00d6d72);
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid Channels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid Rate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid Bits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid Bytes = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid Align = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    /// <summary>MF_MT_AUDIO_CHANNEL_MASK: WAVEFORMATEXTENSIBLE speaker bits, which also give the interleaving order.</summary>
    private static readonly Guid ChannelMask = new("55fb5765-644a-4caf-8479-938983bb1588");
    /// <summary>MF_MT_USER_DATA: 12 bytes of HEAACWAVEINFO after its WAVEFORMATEX, then the AudioSpecificConfig.</summary>
    private static readonly Guid UserDataKey = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    /// <summary>
    /// 5.1 in WAVEFORMATEXTENSIBLE order: FL FR FC LFE BL BR (KSAUDIO_SPEAKER_5POINT1), the mask Microsoft's AAC decoder
    /// documents for channelConfiguration 6 (SCE C, CPE L/R, CPE Ls/Rs, LFE).
    /// </summary>
    internal const uint Surround51Mask = 0x3f;

    /// <summary>AAC-LC, 48 kHz, channelConfiguration 6: the only 5.1 stream verified on a HomePod (2026-10-28).</summary>
    internal static readonly byte[] Surround51Asc = [0x11, 0xb0];

    public int ChannelCount => _channels;
    public int SampleRate => _rate;

    /// <summary>The output type the transform accepted, for the log.</summary>
    internal string Settings { get; } = "";

    public AacEncoder() : this(2, 44100) { }

    /// <param name="channels">2, or 6 for 5.1 (interleaved FL FR FC LFE BL BR). Windows' encoder has no 7.1.</param>
    /// <param name="sampleRate">44100 or 48000, the only rates the encoder takes.</param>
    internal AacEncoder(int channels, int sampleRate)
    {
        // Microsoft's AAC encoder documentation (learn.microsoft.com/windows/win32/medfound/aac-encoder): 1, 2 or 6
        // channels (6 since Windows 10), 44.1 or 48 kHz, 16-bit PCM only.
        if (channels == 8)
            throw new NotSupportedException("Windows' AAC encoder (Media Foundation) takes 1, 2 or 6 channels only: 7.1 (8 channels, bufferStream bit 40) can't be encoded with it");
        if (channels is not (2 or 6)) throw new ArgumentOutOfRangeException(nameof(channels), channels, "AAC encoder: 2 or 6 channels");
        if (sampleRate is not (44100 or 48000)) throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "AAC encoder: 44100 or 48000 Hz");
        _channels = channels; _rate = sampleRate;
        try
        {
            Check(CoInitializeEx(0, 0)); _com = true;
            Check(MFStartup(0x20070, 1)); _mf = true;
            Guid clsid = new("93af0c51-2275-45d2-a35b-f2ba21caed00"), iid = new("bf94c121-5b05-4e6f-8000-ba598961414d");
            Check(CoCreateInstance(&clsid, 0, 1, &iid, out _transform));
            if (channels == 2)
            {
                Check(TrySetTypes(24000, 0x29, null)); // 192 kbit/s AAC-LC
                Settings = $"2 ch, {sampleRate} Hz, 24000 B/s, profile 0x29";
            }
            else Settings = SetSurroundTypes();
            Message(0x10000000); Message(0x10000003);
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// 6 channels, always with mask 0x3F: without it the transform might pick another channel order, and only 0x3F with
    /// ASC 11 B0 was verified on a HomePod (2026-10-28). 72000 B/s is the documented default (12000..24000 times 6).
    /// Profile 0x29 (L2) is a two-channel level, so L4 (0x2A, as Microsoft's 5.1 decoder example) comes first; the rest
    /// are fallbacks, each logged in Settings. On the maintainer's PC the first combination was accepted.
    /// </summary>
    private string SetSurroundTypes()
    {
        var refused = new List<string>();
        foreach (uint bytes in new uint[] { 72000, 144000 })
            foreach (uint? profile in new uint?[] { 0x2a, 0x2b, 0x29, null })
            {
                int hr = TrySetTypes(bytes, profile, Surround51Mask);
                string what = $"6 ch, {_rate} Hz, {bytes} B/s, profile {(profile is { } p ? $"0x{p:X2}" : "unset")}, channel mask 0x{Surround51Mask:X2}";
                if (hr >= 0) return refused.Count == 0 ? what : $"{what} (refused first: {string.Join("; ", refused)})";
                refused.Add($"{what}: 0x{hr:X8}");
            }
        throw new NotSupportedException($"Windows' AAC encoder refused every 6-channel type: {string.Join("; ", refused)}");
    }

    /// <summary>Output type first, then input, as this class always did; returns the first failing HRESULT.</summary>
    private int TrySetTypes(uint avgBytes, uint? profile, uint? mask)
    {
        nint input = 0, output = 0;
        try
        {
            Check(MFCreateMediaType(out input)); Check(MFCreateMediaType(out output));
            SetGuid(input, Major, Audio); SetGuid(input, Subtype, new("00000001-0000-0010-8000-00aa00389b71"));
            SetUInt(input, Channels, (uint)_channels); SetUInt(input, Rate, (uint)_rate); SetUInt(input, Bits, 16);
            SetUInt(input, Bytes, (uint)(_rate * _channels * 2)); SetUInt(input, Align, (uint)(_channels * 2));
            if (mask is { } inputMask) SetUInt(input, ChannelMask, inputMask);
            SetGuid(output, Major, Audio); SetGuid(output, Subtype, new("00001610-0000-0010-8000-00aa00389b71"));
            SetUInt(output, Channels, (uint)_channels); SetUInt(output, Rate, (uint)_rate); SetUInt(output, Bits, 16);
            SetUInt(output, Bytes, avgBytes);
            SetUInt(output, new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), 0);
            if (profile is { } level) SetUInt(output, new("7632f0e6-9538-4d61-acda-ea29c8c14456"), level);
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, 16))(_transform, 0, output, 0);
            if (hr < 0) return hr;
            return ((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, 15))(_transform, 0, input, 0);
        }
        finally { Release(input); Release(output); }
    }

    /// <param name="pcm">1024 interleaved frames of <see cref="ChannelCount"/> channels.</param>
    public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> pcm)
    {
        RequireThread();
        if (_transform == 0) throw new ObjectDisposedException(nameof(AacEncoder));
        if (pcm.Length != 1024 * _channels)
            throw new ArgumentException(_channels == 2 ? "AAC input is 1024 stereo frames" : $"AAC input is 1024 frames of {_channels} channels", nameof(pcm));
        int size = 1024 * _channels * 2;
        nint sample = 0, buffer = 0;
        try
        {
            Check(MFCreateSample(out sample)); Check(MFCreateMemoryBuffer((uint)size, out buffer));
            byte* data; uint max, current;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &current));
            MemoryMarshal.AsBytes(pcm).CopyTo(new Span<byte>(data, size));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)size));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            long begin = _frames * 10_000_000 / _rate, end = (_frames + 1024) * 10_000_000 / _rate;
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, begin));
            Check(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, end - begin));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, 24))(_transform, 0, sample, 0));
            _frames += 1024;
        }
        finally { Release(sample); Release(buffer); }
        var packets = new List<byte[]>();
        while (ReadOutput() is { } packet) packets.Add(packet);
        return packets;
    }

    /// <summary>The output type's MF_MT_USER_DATA (HEAACWAVEINFO tail + AudioSpecificConfig).</summary>
    internal byte[] UserData()
    {
        RequireThread();
        nint type = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_transform, 18))(_transform, 0, &type));
            return GetBlob(type, UserDataKey);
        }
        finally { Release(type); }
    }

    /// <summary>The AudioSpecificConfig after the 12-byte HEAACWAVEINFO tail: 12 10 for 44.1 kHz stereo, 11 B0 for 48 kHz 5.1.</summary>
    internal byte[] AudioSpecificConfig() => UserData() is { Length: > 12 } data ? data[12..] : [];

    private static readonly object SurroundCheckLock = new();
    private static bool? _surroundOk;

    /// <summary>
    /// Whether this PC can encode the HomePod's 5.1 stream, checked once per process before any network traffic: null when
    /// it can, otherwise the translated reason. Windows N/KN without the Media Feature Pack, or a runner whose transform
    /// refuses 6-channel types, can't. The first call builds a (6, 48000) encoder on its own MTA thread (at most 5 s) and
    /// requires the verified ASC; the detail goes to the log once.
    /// </summary>
    internal static string? SurroundProblem()
    {
        lock (SurroundCheckLock)
        {
            if (_surroundOk == null)
            {
                string? detail = null;
                bool ok = false;
                var probe = new Thread(() =>
                {
                    try
                    {
                        using var encoder = new AacEncoder(6, 48000);
                        byte[] asc = encoder.AudioSpecificConfig();
                        ok = asc.AsSpan().SequenceEqual(Surround51Asc);
                        detail = $"{encoder.Settings}, AudioSpecificConfig {Convert.ToHexString(asc)}";
                    }
                    catch (Exception ex) { detail = $"{ex.GetType().Name}: {ex.Message}"; }
                }) { IsBackground = true, Name = "AAC 5.1 check" };
                probe.SetApartmentState(ApartmentState.MTA);
                probe.Start();
                bool finished = probe.Join(TimeSpan.FromSeconds(5));
                _surroundOk = finished && ok;
                string what = finished ? detail ?? "" : "no answer within 5 s";
                if (_surroundOk == true) Log.Info($"AAC 5.1 encoder: {what}");
                else Log.Warn($"AAC 5.1 encoder unavailable, 5.1 surround falls back to stereo: {what}");
            }
            return _surroundOk == true ? null : L.T("Windows 的 AAC 编码器不支持 5.1");
        }
    }

    private byte[]? ReadOutput()
    {
        OutputInfo info;
        Check(((delegate* unmanaged[Stdcall]<nint, uint, OutputInfo*, int>)Slot(_transform, 7))(_transform, 0, &info));
        var output = new OutputBuffer();
        nint memory = 0, contiguous = 0;
        try
        {
            if ((info.Flags & 0x100) == 0)
            {
                Check(MFCreateSample(out output.Sample));
                Check(MFCreateMemoryBuffer(Math.Max(info.Size, 8192), out memory));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(output.Sample, 42))(output.Sample, memory));
            }
            uint status;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, OutputBuffer*, uint*, int>)Slot(_transform, 25))(_transform, 0, 1, &output, &status);
            if (hr == NeedInput) return null;
            Check(hr);
            Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(output.Sample, 41))(output.Sample, &contiguous));
            byte* data; uint max, length;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(contiguous, 3))(contiguous, &data, &max, &length));
            var result = new ReadOnlySpan<byte>(data, checked((int)length)).ToArray();
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(contiguous, 4))(contiguous));
            return result;
        }
        finally { Release(contiguous); Release(memory); Release(output.Sample); Release(output.Events); }
    }

    /// <summary>IMFAttributes::GetBlobSize (slot 14) and GetBlob (15); empty when the key is missing.</summary>
    private static byte[] GetBlob(nint obj, Guid key)
    {
        uint size;
        if (((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(obj, 14))(obj, &key, &size) < 0) return [];
        var blob = new byte[size];
        fixed (byte* data = blob)
        {
            uint written;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, byte*, uint, uint*, int>)Slot(obj, 15))(obj, &key, data, size, &written));
            return blob[..(int)written];
        }
    }

    private void Message(uint message) => Check(((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(_transform, 23))(_transform, message, 0));
    private static void SetGuid(nint obj, Guid key, Guid value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(obj, 24))(obj, &key, &value));
    private static void SetUInt(nint obj, Guid key, uint value) => Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(obj, 21))(obj, &key, value));
    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    private static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj); }
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private void RequireThread() { if (Environment.CurrentManagedThreadId != _thread) throw new InvalidOperationException("Use the AAC encoder on its owning thread"); }
    public void Dispose()
    {
        RequireThread();
        Release(_transform); _transform = 0;
        if (_mf) { MFShutdown(); _mf = false; }
        if (_com) { CoUninitialize(); _com = false; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct OutputInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputBuffer { public uint Stream; public nint Sample; public uint Status; public nint Events; }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint result);
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint result);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint result);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint size, out nint result);
}
