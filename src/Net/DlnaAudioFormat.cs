using System.Buffers.Binary;

namespace HomePodCast.Net;

internal enum DlnaAudioFormat { L16, Wav }
internal enum DlnaHttpFraming { Chunked, LargeContentLength }

/// <summary>Fills 352 stereo frames at 44,100 Hz; underflow is silence, not end of stream.</summary>
internal delegate void DlnaPcmReader(long sendTicks, Span<short> stereo);

internal static class DlnaAudioFormats
{
    public const int SampleRate = 44_100;
    public const int Channels = 2;
    public const int FramesPerChunk = 352;
    public const int BytesPerFrame = 4;
    // RIFF has uint32 lengths: even a chunked WAV must end after this many PCM bytes (about 6.8 hours).
    public const uint MaxWavDataLength = uint.MaxValue - 39;
    // Sender paced, streaming transfer mode, DLNA 1.5. No seek, background or pause capability is advertised.
    // Flag values: https://gnome.pages.gitlab.gnome.org/gupnp-av/docs/flags.DLNAFlags.html
    public const string ContentFeatures = "DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=81100000000000000000000000000000";

    public static string ContentType(DlnaAudioFormat format) => format switch
    {
        DlnaAudioFormat.L16 => "audio/L16;rate=44100;channels=2",
        DlnaAudioFormat.Wav => "audio/wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string Extension(DlnaAudioFormat format) => format switch
    {
        DlnaAudioFormat.L16 => "l16",
        DlnaAudioFormat.Wav => "wav",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static string ProtocolInfo(DlnaAudioFormat format) => $"http-get:*:{ContentType(format)}:{ContentFeatures}";

    public static byte[] WavHeader(uint dataLength)
    {
        if (dataLength > MaxWavDataLength || dataLength % BytesPerFrame != 0)
            throw new ArgumentOutOfRangeException(nameof(dataLength));
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), dataLength + 36);
        "WAVEfmt "u8.CopyTo(header.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), SampleRate * BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        "data"u8.CopyTo(header.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), dataLength);
        return header;
    }
}
