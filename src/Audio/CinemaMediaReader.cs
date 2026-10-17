using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// Native Windows Media Foundation audio decode of a local file: up to 8 channels as 32-bit float, the decoder's own
/// speaker mask kept. Which containers and codecs work is Windows' business (WAV and AAC in MP4 always; AC-3/E-AC-3
/// only where Windows has the Dolby decoder). Uses the documented IMFSourceReader/IMFAttributes/IMFSample vtables without
/// a runtime package dependency. Created, read and disposed on the same MTA decoder thread. No video, shell, network URL
/// or external player.
/// </summary>
internal sealed unsafe class CinemaMediaReader : IDisposable
{
    private nint _reader;
    private bool _started, _com;
    private const uint Audio = 0xfffffffd; // MF_SOURCE_READER_FIRST_AUDIO_STREAM
    private const uint FlagError = 0x1, FlagEnd = 0x2, FlagNativeChanged = 0x10, FlagCurrentChanged = 0x20;
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid AudioType = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid FloatType = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid ChannelsKey = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid RateKey = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MaskKey = new("55fb5765-644a-4caf-8479-938983bb1588");
    public int Channels { get; }
    public int Rate { get; }

    /// <summary>The decoder's speaker mask (0: it named none); may change at a format change (see <see cref="Read"/>).</summary>
    public uint Mask { get; private set; }

    // Media Foundation stays started from the first open on. Before 2026-10-17 each reader started it and its Dispose shut
    // it down again whenever no other user held it, so a file open created Media Foundation's work-queue threads and the
    // close waited for them to end. Measured 2026-10-17 on 4 CPUs: an open took 29 ms that way and 0.8 ms with the
    // platform kept; with four above-normal threads keeping those CPUs busy, 1.2 s (up to 2.9 s) against 2 ms. On
    // 2026-10-16 (CI run 38970600060) a 10 s and a 20 s wait for such opens ran out together, both still in the open.
    private static int s_platformKept;

    private static void KeepPlatform()
    {
        if (Interlocked.CompareExchange(ref s_platformKept, 1, 0) == 0 && MFStartup(0x20070, 0) < 0) Volatile.Write(ref s_platformKept, 0);
    }

    /// <param name="step">Told each native step before it starts, so a timeout can say where the open waits.</param>
    /// <exception cref="CinemaException">Not a local file, no decoder, or not a layout the cinema plays.</exception>
    public CinemaMediaReader(string path, Action<string>? step = null)
    {
        // A path, never a URL: Media Foundation would otherwise fetch it from the network.
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new CinemaException(L.T("选的文件不存在或打不开。"), "not an existing file path");
        nint native = 0, type = 0, current = 0;
        try
        {
            step?.Invoke("CoInitializeEx");
            Check(CoInitializeEx(0, 0), "CoInitializeEx"); _com = true; // MTA; S_FALSE (already) still needs its CoUninitialize
            step?.Invoke("MFStartup");
            Check(MFStartup(0x20070, 0), "MFStartup"); _started = true;
            KeepPlatform();
            try
            {
                step?.Invoke("MFCreateSourceReaderFromURL");
                Check(MFCreateSourceReaderFromURL(path, 0, out _reader), "MFCreateSourceReaderFromURL");
                step?.Invoke("the media type");
                Check(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Method(_reader, 5))(_reader, Audio, 0, &native), "GetNativeMediaType");
            }
            catch (COMException ex)
            {
                throw new CinemaException(L.T("这个文件在这台电脑上无法解码：Windows 没有它的解码器，或者文件已损坏。"),
                    $"no audio decoder: 0x{ex.HResult:x8} {ex.Message}", ex);
            }
            int channels = checked((int)GetUInt(native, ChannelsKey));
            uint rate = GetUInt(native, RateKey), mask = GetUInt(native, MaskKey, optional: true);
            if (CinemaChannels.Resolve(channels, mask) == null) throw CinemaException.UnsupportedLayout(channels, mask);
            Check(MFCreateMediaType(out type), "MFCreateMediaType");
            SetGuid(type, Major, AudioType); SetGuid(type, Subtype, FloatType);
            SetUInt(type, ChannelsKey, (uint)channels); SetUInt(type, RateKey, rate);
            if (mask != 0) SetUInt(type, MaskKey, mask);
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, int>)Method(_reader, 7))(_reader, Audio, 0, type), "SetCurrentMediaType");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(_reader, 6))(_reader, Audio, &current), "GetCurrentMediaType");
            Channels = checked((int)GetUInt(current, ChannelsKey)); Rate = checked((int)GetUInt(current, RateKey));
            Mask = GetUInt(current, MaskKey, true);
            if (Channels != channels || Rate is < 8000 or > 192000 || CinemaChannels.Resolve(Channels, Mask) == null)
                throw CinemaException.UnsupportedLayout(Channels, Mask);
            // Disable every other stream; decoding a movie does not decode or display its video.
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Method(_reader, 4))(_reader, 0xfffffffe, 0), "SetStreamSelection");
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int, int>)Method(_reader, 4))(_reader, Audio, 1), "SetStreamSelection");
        }
        catch (COMException ex)
        {
            Dispose();
            throw new CinemaException(L.T("这个文件在这台电脑上无法解码：Windows 没有它的解码器，或者文件已损坏。"),
                $"decoder setup: 0x{ex.HResult:x8} {ex.Message}", ex);
        }
        catch { Dispose(); throw; }
        finally { Release(native); Release(type); Release(current); }
    }

    /// <summary>
    /// The file's channels and speaker mask, read on an MTA thread of its own (Media Foundation's, as the decoder's),
    /// never on a pool thread: its native open may take long, and pool threads' COM state isn't ours to set and undo.
    /// </summary>
    /// <param name="step">As for the constructor, on the inspection thread.</param>
    internal static Task<(int Channels, uint Mask)> InspectAsync(string path, Action<string>? step = null)
    {
        var result = new TaskCompletionSource<(int, uint)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new CinemaMediaReader(path, step);
                result.TrySetResult((reader.Channels, reader.Mask));
            }
            catch (Exception ex) { result.TrySetException(ex); }
        }) { IsBackground = true, Name = "Cinema file inspection" };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return result.Task;
    }

    /// <summary>Where an open is and since when, for the message of a wait that gave up.</summary>
    internal sealed class Progress(string stage)
    {
        private volatile string _stage = stage;
        private long _since = Environment.TickCount64;

        public void Step(string stage)
        {
            _stage = stage;
            Volatile.Write(ref _since, Environment.TickCount64);
        }

        public override string ToString() =>
            FormattableString.Invariant($"{_stage} for {(Environment.TickCount64 - Volatile.Read(ref _since)) / 1000.0:F1} s");
    }

    /// <summary>The next decoded frames (interleaved floats), or null at the end of the file.</summary>
    /// <exception cref="CinemaException">A decode error, or the format changed to one this playback cannot follow.</exception>
    public float[]? Read()
    {
        while (true)
        {
            uint actual = 0, flags = 0; long timestamp = 0; nint sample = 0, buffer = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint*, uint*, long*, nint*, int>)Method(_reader, 9))
                (_reader, Audio, 0, &actual, &flags, &timestamp, &sample);
            try
            {
                if (hr < 0 || (flags & FlagError) != 0)
                    throw new CinemaException(L.T("播放中解码出错，已停止。"), $"ReadSample failed: 0x{hr:x8}, flags 0x{flags:x}");
                // Some AAC and HE-AAC files report a "changed" format with their first frames (review of #22). Read it again:
                // the same channels and rate go on (a new mask rebuilds the channel matrix); anything else stops.
                if ((flags & (FlagNativeChanged | FlagCurrentChanged)) != 0) FormatChanged(flags);
                if (sample == 0) { if ((flags & FlagEnd) != 0) return null; continue; }
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Method(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
                byte* pointer = null; uint max = 0, size = 0;
                Check(((delegate* unmanaged[Stdcall]<nint, byte**, uint*, uint*, int>)Method(buffer, 3))(buffer, &pointer, &max, &size), "Lock");
                try
                {
                    if (size % (Channels * sizeof(float)) != 0 || size > 16 * 1024 * 1024)
                        throw new CinemaException(L.T("播放中解码出错，已停止。"), $"decoded buffer of {size} bytes for {Channels} channels");
                    var output = new float[size / sizeof(float)];
                    new ReadOnlySpan<float>(pointer, output.Length).CopyTo(output);
                    for (int i = 0; i < output.Length; i++)
                        if (!float.IsFinite(output[i])) output[i] = 0; // one bad sample is not worth stopping the film
                    return output;
                }
                finally { Check(((delegate* unmanaged[Stdcall]<nint, int>)Method(buffer, 4))(buffer), "Unlock"); }
            }
            catch (COMException ex)
            {
                throw new CinemaException(L.T("播放中解码出错，已停止。"), $"decode: 0x{ex.HResult:x8} {ex.Message}", ex);
            }
            finally { Release(buffer); Release(sample); }
        }
    }

    /// <summary>Raised on the decoder thread when a format change kept the channels and rate but named another mask.</summary>
    public event Action<uint>? MaskChanged;

    private void FormatChanged(uint flags)
    {
        nint current = 0;
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(_reader, 6))(_reader, Audio, &current), "GetCurrentMediaType");
            int channels = checked((int)GetUInt(current, ChannelsKey)), rate = checked((int)GetUInt(current, RateKey));
            uint mask = GetUInt(current, MaskKey, true);
            switch (Follow(Channels, Rate, Mask, channels, rate, mask))
            {
                case FormatChange.Same:
                    Log.Debug($"cinema: decoder format change (flags 0x{flags:x}), same layout");
                    break;
                case FormatChange.NewMask:
                    Log.Info($"cinema: decoder speaker mask changed 0x{Mask:x} → 0x{mask:x}");
                    Mask = mask;
                    MaskChanged?.Invoke(mask);
                    break;
                default:
                    throw new CinemaException(L.T("文件在播放中换了声道数或采样率，已停止。"),
                        $"format changed during playback: {Channels} ch {Rate} Hz → {channels} ch {rate} Hz, mask 0x{mask:x}");
            }
        }
        finally { Release(current); }
    }

    internal enum FormatChange { Same, NewMask, Unsupported }

    /// <summary>What a mid-stream format change means: the decoder's output buffers and resamplers only follow the same
    /// channel count and rate; a different mask with those is a new channel matrix.</summary>
    internal static FormatChange Follow(int channels, int rate, uint mask, int newChannels, int newRate, uint newMask)
    {
        if (newChannels != channels || newRate != rate) return FormatChange.Unsupported;
        uint? before = CinemaChannels.Resolve(channels, mask), after = CinemaChannels.Resolve(newChannels, newMask);
        if (after == null) return FormatChange.Unsupported;
        return before == after ? FormatChange.Same : FormatChange.NewMask;
    }

    private static nint Method(nint obj, int slot) => (*(nint**)obj)[slot];
    private static uint GetUInt(nint type, Guid key, bool optional = false)
    {
        uint result = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Method(type, 7))(type, &key, &result);
        if (optional && hr < 0) return 0;
        Check(hr, "GetUINT32"); return result;
    }
    private static void SetUInt(nint type, Guid key, uint value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Method(type, 21))(type, &key, value), "SetUINT32");
    private static void SetGuid(nint type, Guid key, Guid value) =>
        Check(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Method(type, 24))(type, &key, &value), "SetGUID");
    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed", hr);
    }
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
