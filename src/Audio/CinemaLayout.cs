using System.Numerics;
using System.Text;
using System.Text.Json.Serialization;
using HomePodCast.Net;

namespace HomePodCast.Audio;

// Wireless cinema (experimental since 1.1, never tested on real hardware: the maintainer has one HomePod, so the label
// stays until it has been). One HomePod per channel of a local 5.1/7.1 file; see docs/WIRELESS_CINEMA.md.

/// <summary>A position in the room. Subwoofer: a HomePod that plays only the LFE channel (HomePods have no sub output).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CinemaRole>))]
public enum CinemaRole { FrontLeft, FrontRight, Center, Subwoofer, RearLeft, RearRight, SideLeft, SideRight, Stereo }

public sealed class CinemaSpeaker
{
    public CinemaRole Role { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Host { get; set; }
    public int DelayMs { get; set; }
    public int VolumeOffset { get; set; }
}

public sealed class CinemaSettings
{
    public List<CinemaSpeaker> Speakers { get; set; } = [];
    public string? CalibrationMicId { get; set; }
}

/// <summary>
/// The source channels of a file, by WAVEFORMATEXTENSIBLE speaker mask: the floor positions up to 7.1 and any subset of
/// them (mono, stereo, 5.1 with rear or side surrounds, 6.1, 7.1). Channels come in mask bit order.
/// </summary>
internal static class CinemaChannels
{
    public const uint FL = 0x1, FR = 0x2, FC = 0x4, LFE = 0x8, BL = 0x10, BR = 0x20, FLC = 0x40, FRC = 0x80, BC = 0x100,
        SL = 0x200, SR = 0x400;
    public const uint Surround71 = FL | FR | FC | LFE | BL | BR | SL | SR; // 0x63f, KSAUDIO_SPEAKER_7POINT1_SURROUND
    public const uint Supported = Surround71 | FLC | FRC | BC;            // no top (height) speakers
    public const int MaxChannels = 8;

    /// <summary>
    /// The mask to use for a decoder's channel count and mask: its own when it names exactly that many supported
    /// positions; without one (0) the usual layout for that many channels (as FFmpeg's defaults); null when the layout is
    /// not supported (top speakers, more than 8 channels, or a mask that does not match the count).
    /// </summary>
    public static uint? Resolve(int channels, uint mask)
    {
        if (channels is < 1 or > MaxChannels) return null;
        if (mask == 0)
            return channels switch
            {
                1 => FC, 2 => FL | FR, 3 => FL | FR | FC, 4 => FL | FR | BL | BR, 5 => FL | FR | FC | BL | BR,
                6 => 0x3f, 7 => FL | FR | FC | LFE | BC | SL | SR, _ => Surround71,
            };
        return (mask & ~Supported) == 0 && BitOperations.PopCount(mask) == channels ? mask : null;
    }

    /// <summary>For the log and the window: 7.1, 5.1 (side), stereo, … with the mask.</summary>
    public static string Describe(int channels, uint mask)
    {
        string name = mask switch
        {
            FC => "mono",
            FL | FR => "stereo",
            0x3f => "5.1",
            0x60f => "5.1 (side)",
            0x70f => "6.1",
            Surround71 => "7.1",
            0xff => "7.1 (wide)",
            _ => $"{channels} ch",
        };
        return $"{name} (0x{mask:x})";
    }

    /// <summary>For the window: 单声道, 立体声, 5.1, 7.1, or so many channels.</summary>
    public static string DisplayName(int channels, uint mask) => mask switch
    {
        FC => L.T("单声道"),
        FL | FR => L.T("立体声"),
        0x3f or 0x60f => "5.1",
        0x70f => "6.1",
        Surround71 or 0xff => "7.1",
        _ => L.F("{0} 声道", channels),
    };

    /// <summary>The source channel positions, in channel order.</summary>
    public static uint[] Positions(uint mask)
    {
        var list = new List<uint>();
        for (uint bit = 1; bit != 0 && bit <= mask; bit <<= 1)
            if ((mask & bit) != 0) list.Add(bit);
        return [.. list];
    }

    public static string Short(uint position) => position switch
    {
        FL => "FL", FR => "FR", FC => "C", LFE => "LFE", BL => "BL", BR => "BR", FLC => "FLC", FRC => "FRC", BC => "BC",
        SL => "SL", SR => "SR", _ => $"0x{position:x}",
    };
}

/// <summary>
/// Which source channel each speaker plays, at what gain. A channel goes to the speaker of its own position; where
/// that position has no speaker it is folded (2026-10-14, after the review of #22: a 5.1 file with rear surrounds,
/// mask 0x3f, left speakers set to 侧左/侧右 silent):
/// <list type="bullet">
/// <item>Surrounds, side and rear alike: a file with only one pair (5.1) plays it on whichever surround speakers there
/// are, on both pairs at −3 dB each when the room has side and rear speakers; a file with both pairs (7.1) in a room
/// with one pair plays the missing pair on it at −3 dB; with no surround speaker at all, on the front of the same
/// side at −3 dB (ITU-R BS.775 downmix).</item>
/// <item>Center: on the front pair at −3 dB each.</item>
/// <item>LFE: on the HomePod chosen as 低音声道 (it plays nothing else), otherwise on the front pair at −6 dB each,
/// which adds up to about the channel's own level there (a theater sub's +10 dB is left out: no headroom for it).</item>
/// <item>Rarer positions: front-left/right-of-center (the old 7.1 mask 0xff) between the front and the center at −3 dB
/// each; back center (6.1) half on each side's rear, else side, else front speaker.</item>
/// <item>Stereo and mono: the front pair only (mono is the center channel). Nothing is upmixed.</item>
/// </list>
/// The whole matrix is then scaled by one fixed factor so that the busiest speaker cannot clip (the largest sum of
/// gains on one speaker becomes 1): the balance between speakers stays as mixed, and nothing depends on the signal.
/// </summary>
internal sealed class CinemaMatrix
{
    private const float Minus3Db = 0.70710678f, Minus6Db = 0.5f;
    private static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

    private readonly float[][] _rows;
    private readonly float[] _stereoLeft, _stereoRight;
    private readonly IReadOnlyList<CinemaSpeaker> _speakers;
    private readonly uint[] _positions;

    public int Channels { get; }
    public uint Mask { get; }
    public int Count => _rows.Length;

    /// <summary>The factor every gain was scaled by (1 = no headroom needed).</summary>
    public float Scale { get; }

    /// <exception cref="CinemaException">The layout of speakers or the file's channels is not usable.</exception>
    public CinemaMatrix(int channels, uint mask, IReadOnlyList<CinemaSpeaker> speakers)
    {
        Validate(speakers);
        Mask = CinemaChannels.Resolve(channels, mask) ?? throw CinemaException.UnsupportedLayout(channels, mask);
        Channels = channels;
        _speakers = speakers;
        _positions = CinemaChannels.Positions(Mask);
        _rows = speakers.Select(_ => new float[channels]).ToArray();
        _stereoLeft = new float[channels]; _stereoRight = new float[channels];

        var roles = speakers.Select((s, i) => (s.Role, i)).Where(x => x.Role != CinemaRole.Stereo).ToDictionary(x => x.Role, x => x.i);
        for (int c = 0; c < _positions.Length; c++)
        {
            foreach (var (role, gain) in Route(_positions[c], Mask, roles.ContainsKey))
                if (roles.TryGetValue(role, out var target)) _rows[target][c] += gain;
            foreach (var (role, gain) in Route(_positions[c], Mask, r => r is CinemaRole.FrontLeft or CinemaRole.FrontRight))
                if (role == CinemaRole.FrontLeft) _stereoLeft[c] += gain;
                else if (role == CinemaRole.FrontRight) _stereoRight[c] += gain;
        }

        float busiest = _rows.Max(r => r.Sum(Math.Abs));
        if (speakers.Any(s => s.Role == CinemaRole.Stereo))
            busiest = Math.Max(busiest, Math.Max(_stereoLeft.Sum(Math.Abs), _stereoRight.Sum(Math.Abs)));
        Scale = busiest > 1 ? 1 / busiest : 1;
        if (Scale != 1)
            foreach (var row in _rows)
                for (int c = 0; c < row.Length; c++) row[c] *= Scale;
        for (int c = 0; c < channels; c++) { _stereoLeft[c] *= Scale; _stereoRight[c] *= Scale; }
    }

    /// <summary>Where one source channel plays (before the headroom scale), given the file's mask and the room's speakers.</summary>
    internal static IEnumerable<(CinemaRole Role, float Gain)> Route(uint position, uint mask, Func<CinemaRole, bool> has)
    {
        switch (position)
        {
            case CinemaChannels.FL: return [(CinemaRole.FrontLeft, 1)];
            case CinemaChannels.FR: return [(CinemaRole.FrontRight, 1)];
            case CinemaChannels.FC:
                return has(CinemaRole.Center) ? [(CinemaRole.Center, 1)]
                    : [(CinemaRole.FrontLeft, Minus3Db), (CinemaRole.FrontRight, Minus3Db)];
            case CinemaChannels.LFE:
                return has(CinemaRole.Subwoofer) ? [(CinemaRole.Subwoofer, 1)]
                    : [(CinemaRole.FrontLeft, Minus6Db), (CinemaRole.FrontRight, Minus6Db)];
            case CinemaChannels.BL: return Surround(CinemaRole.RearLeft, CinemaRole.SideLeft, CinemaRole.FrontLeft, (mask & CinemaChannels.SL) != 0, has);
            case CinemaChannels.BR: return Surround(CinemaRole.RearRight, CinemaRole.SideRight, CinemaRole.FrontRight, (mask & CinemaChannels.SR) != 0, has);
            case CinemaChannels.SL: return Surround(CinemaRole.SideLeft, CinemaRole.RearLeft, CinemaRole.FrontLeft, (mask & CinemaChannels.BL) != 0, has);
            case CinemaChannels.SR: return Surround(CinemaRole.SideRight, CinemaRole.RearRight, CinemaRole.FrontRight, (mask & CinemaChannels.BR) != 0, has);
            // Between the front and the center (the old 7.1 mask 0xff names its extra pair so): on both, else the front.
            case CinemaChannels.FLC:
                return has(CinemaRole.Center) ? [(CinemaRole.FrontLeft, Minus3Db), (CinemaRole.Center, Minus3Db)] : [(CinemaRole.FrontLeft, 1)];
            case CinemaChannels.FRC:
                return has(CinemaRole.Center) ? [(CinemaRole.FrontRight, Minus3Db), (CinemaRole.Center, Minus3Db)] : [(CinemaRole.FrontRight, 1)];
            // Back center (6.1): half on each side's rear speaker, else its side speaker, else its front (−3 dB more there).
            case CinemaChannels.BC:
                return [BackCenterHalf(CinemaRole.RearLeft, CinemaRole.SideLeft, CinemaRole.FrontLeft, has),
                        BackCenterHalf(CinemaRole.RearRight, CinemaRole.SideRight, CinemaRole.FrontRight, has)];
            default: return [];
        }
    }

    private static (CinemaRole, float) BackCenterHalf(CinemaRole rear, CinemaRole side, CinemaRole front, Func<CinemaRole, bool> has) =>
        has(rear) ? (rear, Minus3Db) : has(side) ? (side, Minus3Db) : (front, Minus6Db);

    /// <param name="own">The speaker position of this surround channel.</param>
    /// <param name="other">The other surround position on the same side (side ↔ rear).</param>
    /// <param name="otherInFile">The file has a channel of its own for <paramref name="other"/> (7.1).</param>
    private static IEnumerable<(CinemaRole, float)> Surround(CinemaRole own, CinemaRole other, CinemaRole front, bool otherInFile,
        Func<CinemaRole, bool> has)
    {
        bool hasOwn = has(own), hasOther = has(other);
        if (hasOwn && hasOther) return otherInFile ? [(own, 1)] : [(own, Minus3Db), (other, Minus3Db)];
        if (hasOwn) return [(own, 1)];
        if (hasOther) return [(other, otherInFile ? Minus3Db : 1)];
        return [(front, Minus3Db)];
    }

    /// <exception cref="CinemaException">Not a layout the cinema can play to.</exception>
    public static void Validate(IReadOnlyList<CinemaSpeaker> speakers)
    {
        if (speakers.Count is < 1 or > 8 || (!speakers.Any(s => s.Role == CinemaRole.Stereo) &&
            (!speakers.Any(s => s.Role == CinemaRole.FrontLeft) || !speakers.Any(s => s.Role == CinemaRole.FrontRight))))
            throw new CinemaException(L.T("至少要给前左和前右各选一台音箱。"), "layout: a full-stereo output or front left and front right are needed");
        if (speakers.Any(s => string.IsNullOrWhiteSpace(s.DeviceId) || StereoPairs.IsPairId(s.DeviceId)))
            throw new CinemaException(L.T("立体声对不能用在无线影院里，请选单独的 HomePod。"), "layout: a stereo pair or an empty device id");
        if (speakers.Where(s => s.Role != CinemaRole.Stereo).Select(s => s.Role).Distinct().Count() != speakers.Count(s => s.Role != CinemaRole.Stereo) ||
            speakers.Select(s => GroupPlan.Key(s.DeviceId)).Distinct().Count() != speakers.Count)
            throw new CinemaException(L.T("每个位置只能选一台音箱，一台音箱也只能放在一个位置。"), "layout: a role or a speaker is used twice");
        if (speakers.Any(s => s.DelayMs is < 0 or > 500))
            throw new CinemaException(L.T("音箱延迟要在 0 到 500 毫秒之间。"), "layout: a delay outside 0-500 ms");
    }

    /// <summary>The gain of source channel <paramref name="channel"/> on speaker <paramref name="speaker"/> (scaled).</summary>
    public float Gain(int speaker, int channel) => _rows[speaker][channel];

    public float Sample(int speaker, ReadOnlySpan<float> frame)
    {
        var row = _rows[speaker];
        float value = 0;
        for (int c = 0; c < row.Length; c++) value += frame[c] * row[c];
        return Math.Clamp(value, -1, 1); // a decoder may deliver a little over full scale
    }

    public (float Left, float Right) StereoSample(int speaker, ReadOnlySpan<float> frame)
    {
        if (_speakers[speaker].Role != CinemaRole.Stereo) { float mono = Sample(speaker, frame); return (mono, mono); }
        float left = 0, right = 0;
        for (int c = 0; c < Channels; c++) { left += frame[c] * _stereoLeft[c]; right += frame[c] * _stereoRight[c]; }
        return (Math.Clamp(left, -1, 1), Math.Clamp(right, -1, 1));
    }

    /// <summary>For the log: what each speaker plays, e.g. "FrontLeft=FL+C-3dB+LFE-6dB, …, headroom -4.6 dB".</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _rows.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(_speakers[i].Role).Append('=');
            var parts = new List<string>();
            for (int c = 0; c < Channels; c++)
            {
                float gain = _rows[i][c] / Scale;
                if (gain == 0) continue;
                double db = 20 * Math.Log10(gain);
                parts.Add(CinemaChannels.Short(_positions[c]) + (Math.Abs(db) < 0.05 ? "" : string.Create(Invariant, $"{db:+0;-0}dB")));
            }
            sb.Append(parts.Count == 0 ? "silent" : string.Join("+", parts));
        }
        if (Scale != 1) sb.Append(string.Create(Invariant, $", headroom {20 * Math.Log10(Scale):F1} dB"));
        return sb.ToString();
    }
}

/// <summary>
/// A cinema failure the window shows as it is: <see cref="Exception.Message"/> is the translated text (L.T), never an
/// English exception message (review of #22); <see cref="LogText"/> is the English detail for the log.
/// </summary>
internal sealed class CinemaException(string text, string logText, Exception? inner = null) : Exception(text, inner)
{
    public string LogText { get; } = logText;

    public static CinemaException UnsupportedLayout(int channels, uint mask) => new(
        L.T("这个文件的声道布局不支持：只能播放单声道、立体声、5.1 和 7.1 等最多 8 个声道的标准布局。"),
        $"unsupported channel layout: {channels} channels, mask 0x{mask:x}");
}
