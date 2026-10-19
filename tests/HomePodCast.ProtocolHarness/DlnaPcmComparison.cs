using System.Buffers.Binary;

namespace HomePodCast.ProtocolHarness;

internal static class DlnaPcmComparison
{
    public const int MarkerFrame = 22_050;
    private const int ProbeCount = 96, ProbeStride = 29;
    public const int MarkerFrames = ProbeCount * ProbeStride;
    public static short Sample(ReadOnlySpan<byte> pcm, int frame, int channel)
        => BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(frame * 4 + channel * 2, 2));

    // Measure the offset from both channels. Searching every frame rather than assuming AAC/MP3 priming also
    // accommodates different decoder trimming. Sparse probes make this bounded search inexpensive.
    public static (int Frame, double Rms) FindMarker(byte[] expected, int anchor, byte[] actual, int actualLength)
    {
        if (anchor < 0 || (anchor + MarkerFrames) * 4 > expected.Length) throw new ArgumentOutOfRangeException(nameof(anchor));
        int end = Math.Min(actualLength / 4 - MarkerFrames, DlnaCodecFixture.SampleRate * 3);
        long best = long.MaxValue;
        int bestFrame = -1;
        for (int start = 0; start <= end; start++)
        {
            long error = 0;
            for (int probe = 0; probe < ProbeCount; probe++)
            {
                int frame = probe * ProbeStride;
                for (int channel = 0; channel < 2; channel++)
                {
                    int difference = Sample(expected, anchor + frame, channel) - Sample(actual, start + frame, channel);
                    error += (long)difference * difference;
                }
                if (error >= best) break;
            }
            if (error < best) { best = error; bestFrame = start; }
        }
        return (bestFrame, bestFrame < 0 ? double.PositiveInfinity : Math.Sqrt(best / (double)(ProbeCount * 2)));
    }

    public static (double Rms, int Peak) Compare(byte[] reference, int referenceFrame, byte[] actual, int actualFrame, int frames)
    {
        double squares = 0;
        int peak = 0;
        for (int start = 0; start < frames; start += 1024)
        {
            int count = Math.Min(1024, frames - start);
            for (int channel = 0; channel < 2; channel++)
            {
                long windowSquares = 0;
                for (int i = 0; i < count; i++)
                {
                    int difference = Sample(reference, referenceFrame + start + i, channel) - Sample(actual, actualFrame + start + i, channel);
                    peak = Math.Max(peak, Math.Abs(difference));
                    windowSquares += (long)difference * difference;
                }
                double rms = Math.Sqrt(windowSquares / (double)count);
                // Independent decoders round the same lossy bitstream slightly differently. A per-channel,
                // per-block bound exposes missing/repeated samples instead of averaging a discontinuity away.
                if (rms > 16 || peak > 256)
                    throw new InvalidDataException($"Decoded PCM continuity/channel mismatch near frame {start}, channel {channel}: RMS={rms:F2}, peak={peak} (limits 16/256 LSB).");
                squares += windowSquares;
            }
        }
        return (Math.Sqrt(squares / (frames * 2.0)), peak);
    }

    public static void SelfTest()
    {
        const int shift = 137, frames = 44_100;
        byte[] source = new byte[frames * 4], shifted = new byte[(frames + shift) * 4];
        for (int i = 0; i < frames; i++)
            for (int ch = 0; ch < 2; ch++)
                BinaryPrimitives.WriteInt16LittleEndian(source.AsSpan(i * 4 + ch * 2), DlnaCodecFixture.Signal(i, ch));
        source.CopyTo(shifted, shift * 4);
        var marker = FindMarker(source, MarkerFrame, shifted, shifted.Length);
        if (marker.Frame != MarkerFrame + shift || marker.Rms != 0) throw new Exception("DLNA decoded marker alignment failed.");
        Compare(source, MarkerFrame, shifted, marker.Frame, 8192);
        // Allow independent decoder rounding, while retaining enough precision to reject channel swaps and loss.
        for (int i = 0; i < 8192; i++)
            for (int ch = 0; ch < 2; ch++)
                BinaryPrimitives.WriteInt16LittleEndian(shifted.AsSpan((marker.Frame + i) * 4 + ch * 2),
                    (short)(Sample(source, MarkerFrame + i, ch) + (ch == 0 ? 1 : -1)));
        Compare(source, MarkerFrame, shifted, marker.Frame, 8192);
        for (int i = 0; i < 8192; i++)
            for (int ch = 0; ch < 2; ch++)
                BinaryPrimitives.WriteInt16LittleEndian(shifted.AsSpan((marker.Frame + i) * 4 + ch * 2),
                    Sample(source, MarkerFrame + i, 1 - ch));
        RequireMismatch("swapped stereo channels");
        source.AsSpan((MarkerFrame + 2) * 4, 8192 * 4).CopyTo(shifted.AsSpan(marker.Frame * 4));
        RequireMismatch("two-frame loss");

        void RequireMismatch(string reason)
        {
            try { Compare(source, MarkerFrame, shifted, marker.Frame, 8192); }
            catch (InvalidDataException) { return; }
            throw new Exception("DLNA continuity comparison did not detect " + reason + ".");
        }
    }
}
