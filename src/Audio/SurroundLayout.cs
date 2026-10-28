using System.Numerics;

namespace HomePodCast.Audio;

/// <summary>
/// How a Windows endpoint's channels become the HomePod's 5.1 (experimental since 1.4): six output slots in the AAC
/// encoder's order for mask 0x3F, 0 FL, 1 FR, 2 FC, 3 LFE, 4 Ls, 5 Rs. Each source speaker bit goes to its slot: back
/// and side alike to the surround pair (a 5.1 endpoint often reports side speakers, 0x60F, rather than back, 0x3F), the
/// front-centre pair between the front pair and the centre, back centre to both surround slots. Top channels are dropped.
/// </summary>
/// <remarks>
/// Every channel plays in its own slot at unity, so 7.1 (0x63F) folds as Ls = BL + SL, Rs = BR + SR. That keeps the power
/// of uncorrelated content and is exact when one of the two is silent: Chromium, mpv and VLC map a 5.1 film onto a 7.1
/// endpoint's side pair and leave the back pair silent, and a 0.7071 fold would play its surrounds 3 dB quieter than on
/// a 5.1 endpoint. A channel between two slots is split at 0.7071 each (constant power), as CinemaMatrix does: back centre
/// to both surround slots, front left/right of centre to the front and the centre, which leaves the front pair at unity.
/// The fold is needed because Media Foundation's AAC encoder has no 8-channel mode (2026-10-28). Correlated sound in back
/// and side at once can exceed 1; the sender's s16 clamp takes that. LFE stays discrete.
/// </remarks>
internal sealed class SurroundLayout
{
    public const int Slots = 6;
    private const float Split = 0.70710678f;
    private const uint FrontLeft = 0x1, FrontRight = 0x2, FrontCenter = 0x4;
    private const uint FrontLeftOfCenter = 0x40, FrontRightOfCenter = 0x80, BackCenter = 0x100;
    private const uint Back = 0x30, Side = 0x600, BackLeft = 0x10, BackRight = 0x20, SideLeft = 0x200, SideRight = 0x400;
    private static readonly HashSet<uint> LoggedDrops = [];

    /// <summary>Gain of source channel c into slot s at [c * Slots + s].</summary>
    private readonly float[] _gains;

    /// <summary>Source channels per frame.</summary>
    public int Channels { get; }

    /// <summary>The speaker mask the channels were read with (the device's, or the default for its channel count).</summary>
    public uint Mask { get; }

    /// <summary>For the log: "6 ch mask 0x60F → 5.1 (side→surround)".</summary>
    public string Describe { get; }

    private SurroundLayout(int channels, uint mask, float[] gains, string describe)
    {
        Channels = channels; Mask = mask; _gains = gains; Describe = describe;
    }

    /// <summary>The layout of an endpoint's mix format; null with the translated reason when it can't give 5.1.</summary>
    public static SurroundLayout? For(WaveFormat fmt, out string? refusal) => For(fmt.Channels, fmt.ChannelMask, out refusal);

    internal static SurroundLayout? For(int channels, uint mask, out string? refusal)
    {
        // A mask that doesn't describe the channels (0, or another count) is no guide: take the usual layout for the count.
        uint used = mask != 0 && BitOperations.PopCount(mask) == channels ? mask : channels switch { 6 => 0x3F, 8 => 0x63F, _ => 0 };
        var bits = new uint[Math.Max(channels, 0)];
        int index = 0;
        for (uint rest = used; rest != 0 && index < bits.Length; rest &= rest - 1) bits[index++] = rest & ~(rest - 1);

        uint dropped = 0;
        foreach (uint bit in bits)
            if (bit != 0 && Feeds(bit).Length == 0) dropped |= bit;
        // 5.1 needs a channel for each of front left, front right, centre and both surrounds (LFE may be missing).
        bool Has(uint any) => (used & any) != 0;
        if (channels < 6 || !Has(FrontLeft | FrontLeftOfCenter) || !Has(FrontRight | FrontRightOfCenter) || !Has(FrontCenter)
            || !Has(BackLeft | SideLeft | BackCenter) || !Has(BackRight | SideRight | BackCenter))
        {
            refusal = L.F("Windows 输出设备不是 5.1 或 7.1：现在是 {0} 声道", channels);
            return null;
        }

        var gains = new float[channels * Slots];
        for (int c = 0; c < channels; c++)
            foreach (var (slot, gain) in Feeds(bits[c]))
                gains[c * Slots + slot] = gain;

        var notes = new List<string>();
        if (used != mask) notes.Add($"mask taken as 0x{used:X}");
        if ((used & Side) != 0 && (used & Back) == 0) notes.Add("side→surround");
        if ((used & Side) != 0 && (used & Back) != 0) notes.Add("7.1 folded: back+side→surround");
        if ((used & (FrontLeftOfCenter | FrontRightOfCenter)) != 0) notes.Add("front-centre pair→front+centre");
        if ((used & BackCenter) != 0) notes.Add("back centre→both surround");
        if (dropped != 0)
        {
            notes.Add($"dropped 0x{dropped:X}");
            bool first;
            lock (LoggedDrops) first = LoggedDrops.Add(dropped);
            if (first) Log.Debug($"surround layout: channels 0x{dropped:X} of mask 0x{used:X} have no 5.1 slot and are dropped");
        }
        string describe = $"{channels} ch mask 0x{mask:X} → 5.1" + (notes.Count > 0 ? $" ({string.Join(", ", notes)})" : "");
        refusal = null;
        return new SurroundLayout(channels, used, gains, describe);
    }

    /// <summary>Where one source channel plays: its own slot at unity, or split between two at 0.7071 each.</summary>
    private static (int Slot, float Gain)[] Feeds(uint bit) => bit switch
    {
        FrontLeft => [(0, 1f)],
        FrontRight => [(1, 1f)],
        FrontCenter => [(2, 1f)],
        0x8 => [(3, 1f)],
        BackLeft or SideLeft => [(4, 1f)],
        BackRight or SideRight => [(5, 1f)],
        FrontLeftOfCenter => [(0, Split), (2, Split)],
        FrontRightOfCenter => [(1, Split), (2, Split)],
        BackCenter => [(4, Split), (5, Split)],
        _ => [],
    };

    /// <summary>
    /// <paramref name="frames"/> frames of the endpoint's buffer as 6-float 5.1 frames into <paramref name="dst"/>; samples
    /// are read as <see cref="LoopbackCapture.ReadSample"/> reads them (16/24/32-bit PCM, float with NaN cleaned).
    /// </summary>
    public unsafe void Convert(IntPtr data, int frames, WaveFormat fmt, Span<float> dst)
    {
        if (fmt.Channels != Channels) throw new ArgumentException($"layout for {Channels} channels, format has {fmt.Channels}", nameof(fmt));
        byte* p = (byte*)data;
        Span<float> frame = stackalloc float[Channels];
        for (int i = 0; i < frames; i++)
        {
            byte* f = p + (long)i * fmt.BlockAlign;
            for (int c = 0; c < Channels; c++) frame[c] = LoopbackCapture.ReadSample(f, c, fmt);
            Mix(frame, dst.Slice(i * Slots, Slots));
        }
    }

    /// <summary>The same from interleaved float frames of <see cref="Channels"/> channels (tests).</summary>
    public void Convert(ReadOnlySpan<float> interleaved, int frames, Span<float> dst)
    {
        Span<float> frame = stackalloc float[Channels];
        for (int i = 0; i < frames; i++)
        {
            for (int c = 0; c < Channels; c++) frame[c] = SampleGuard.Clean(interleaved[i * Channels + c]);
            Mix(frame, dst.Slice(i * Slots, Slots));
        }
    }

    private void Mix(ReadOnlySpan<float> frame, Span<float> output)
    {
        for (int s = 0; s < Slots; s++)
        {
            float sum = 0;
            for (int c = 0; c < frame.Length; c++) sum += _gains[c * Slots + s] * frame[c];
            output[s] = sum;
        }
    }
}
