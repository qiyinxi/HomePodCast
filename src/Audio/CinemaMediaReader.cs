using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Native Windows Media Foundation audio decode. Only local existing files, and only discrete 6/8-channel audio.
/// Uses the documented IMFSourceReader/IMFAttributes/IMFSample vtables without a runtime package dependency.
/// Created, read and disposed on the same MTA decoder thread. No video, shell, network URL or external player.
/// </summary>
internal sealed unsafe class CinemaMediaReader : IDisposable
{
    private nint _reader;
    private bool _started, _com;
    private const uint Audio = 0xfffffffd;
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatType = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid ChannelsKey = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid RateKey = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MaskKey = new("55fb5765-644a-4caf-8479-938983bb1588");
    public int Channels { get; }
    public int Rate { get; }
    public uint Mask { get; }
    public CinemaMediaReader(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
            throw new ArgumentException("Choose an existing local media file.");
        int com = CoInitializeEx(0, 0);
        Check(com); _com = true;
        nint native = 0, type = 0, current = 0;
        try
        {
            Check(MFStartup(0x20070, 0)); _started = true;
            Check(MFCreateSourceReaderFromURL(path, 0, out _reader));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Method(_reader, 5))(_reader, Audio, 0, &native));
            int channels = checked((int)GetUInt(native, ChannelsKey));
            if (channels is not (6 or 8)) throw new InvalidDataException("The source is not discrete 5.1/7.1 audio (stereo is not upmixed).");
            uint rate = GetUInt(native, RateKey), mask = GetUInt(native, MaskKey, optional: true);
            Check(MFCreateMediaType(out type));
            SetGuid(type, Major, AudioType); SetGuid(type, Subtype, FloatType);
            SetUInt(type, ChannelsKey, (uint)channels); SetUInt(type, RateKey, rate);
            if (mask != 0) SetUInt(type, MaskKey, mask);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Method(_reader, 7))(_reader, Audio, 0, type));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(_reader, 6))(_reader, Audio, &current));
            Channels = checked((int)GetUInt(current, ChannelsKey)); Rate = checked((int)GetUInt(current, RateKey));
            Mask = GetUInt(current, MaskKey, true);
            if (Channels != channels || Rate is < 8000 or > 192000) throw new InvalidDataException("Decoder changed the source layout unexpectedly.");
            // Disable every other stream; decoding a movie does not decode or display its video.
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Method(_reader, 4))(_reader, 0xfffffffe, 0));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Method(_reader, 4))(_reader, Audio, 1));
        }
        catch { Dispose(); throw; }
        finally { Release(native); Release(type); Release(current); }
    }
    public float[]? Read()
    {
        while (true)
        {
            uint actual = 0, flags = 0; long timestamp = 0; nint sample = 0, buffer = 0;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Method(_reader, 9))
                (_reader, Audio, 0, &actual, &flags, &timestamp, &sample));
            try
            {
                if ((flags & 0x30) != 0) throw new InvalidDataException("The source changed media format during playback.");
                if (sample == 0) { if ((flags & 2) != 0) return null; continue; }
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(sample, 41))(sample, &buffer));
                byte* pointer = null; uint max = 0, size = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Method(buffer, 3))(buffer, &pointer, &max, &size));
                try
                {
                    if (size % (Channels * sizeof(float)) != 0 || size > 16 * 1024 * 1024) throw new InvalidDataException("Invalid decoded audio frame size.");
                    var output = new float[size / sizeof(float)];
                    new ReadOnlySpan<float>(pointer, output.Length).CopyTo(output);
                    foreach (float value in output) if (!float.IsFinite(value)) throw new InvalidDataException("Non-finite decoded audio.");
                    return output;
                }
                finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer)); }
            }
            finally { Release(buffer); Release(sample); }
        }
    }
    private static nint Method(nint obj, int slot) => (*(nint**)obj)[slot];
    private static uint GetUInt(nint type, Guid key, bool optional = false)
    {
        uint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Method(type, 7))(type, &key, &result);
        if (optional && hr < 0) return 0;
        Check(hr); return result;
    }
    private static void SetUInt(nint type, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Method(type, 21))(type, &key, value));
    private static void SetGuid(nint type, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(type, 24))(type, &key, &value));
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void Release(nint obj) { if (obj != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Method(obj, 2))(obj); }
    public void Dispose()
    {
        Release(_reader); _reader = 0;
        if (_started) { MFShutdown(); _started = false; }
        if (_com) { CoUninitialize(); _com = false; }
    }
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] private static extern int MFCreateSourceReaderFromURL(string url, nint attributes, out nint reader);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
