using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HomePodCast.Audio;

internal interface IAacEncoder : IDisposable
{
    IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> stereo);
}

/// <summary>
/// Windows' built-in AAC-LC Media Foundation transform. Own it on one MTA thread, including Dispose.
/// Raw 44.1 kHz stereo access units; no bundled executable, codec DLL or registration.
/// ABI and attribute identifiers are from the Windows SDK mfobjects.h / mftransform.h / mfapi.h.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class AacEncoder : IAacEncoder
{
    private nint _transform;
    private bool _com, _mf;
    private long _frames;
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

    public AacEncoder()
    {
        try
        {
            Check(CoInitializeEx(0, 0)); _com = true;
            Check(MFStartup(0x20070, 1)); _mf = true;
            Guid clsid = new("93af0c51-2275-45d2-a35b-f2ba21caed00"), iid = new("bf94c121-5b05-4e6f-8000-ba598961414d");
            Check(CoCreateInstance(&clsid, 0, 1, &iid, out _transform));
            nint input = 0, output = 0;
            try
            {
                Check(MFCreateMediaType(out input)); Check(MFCreateMediaType(out output));
                SetGuid(input, Major, Audio); SetGuid(input, Subtype, new("00000001-0000-0010-8000-00aa00389b71"));
                SetUInt(input, Channels, 2); SetUInt(input, Rate, 44100); SetUInt(input, Bits, 16);
                SetUInt(input, Bytes, 176400); SetUInt(input, Align, 4);
                SetGuid(output, Major, Audio); SetGuid(output, Subtype, new("00001610-0000-0010-8000-00aa00389b71"));
                SetUInt(output, Channels, 2); SetUInt(output, Rate, 44100); SetUInt(output, Bits, 16);
                SetUInt(output, Bytes, 24000); // 192 kbit/s AAC-LC
                SetUInt(output, new("bfbabe79-7434-4d1c-94f0-72a3b9e17188"), 0);
                SetUInt(output, new("7632f0e6-9538-4d61-acda-ea29c8c14456"), 0x29);
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, 16))(_transform, 0, output, 0));
                Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(_transform, 15))(_transform, 0, input, 0));
            }
            finally { Release(input); Release(output); }
            Message(0x10000000); Message(0x10000003);
        }
        catch { Dispose(); throw; }
    }

    public IReadOnlyList<byte[]> Encode(ReadOnlySpan<short> stereo)
    {
        RequireThread();
        if (_transform == 0) throw new ObjectDisposedException(nameof(AacEncoder));
        if (stereo.Length != 2048) throw new ArgumentException("AAC input is 1024 stereo frames", nameof(stereo));
        nint sample = 0, buffer = 0;
        try
        {
            Check(MFCreateSample(out sample)); Check(MFCreateMemoryBuffer(4096, out buffer));
            byte* data; uint max, current;
            Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &current));
            MemoryMarshal.AsBytes(stereo).CopyTo(new Span<byte>(data, 4096));
            Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer));
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, 4096));
            Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            long begin = _frames * 10_000_000 / 44100, end = (_frames + 1024) * 10_000_000 / 44100;
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
