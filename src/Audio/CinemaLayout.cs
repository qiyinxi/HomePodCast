using System.Numerics;
using System.Text.Json.Serialization;
using HomePodCast.Net;

namespace HomePodCast.Audio;

[JsonConverter(typeof(JsonStringEnumConverter<CinemaRole>))]
public enum CinemaRole { FrontLeft, FrontRight, Center, Subwoofer, RearLeft, RearRight, SideLeft, SideRight }

public sealed class CinemaSpeaker
{
    public CinemaRole Role { get; set; }
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Host { get; set; }
    public int DelayMs { get; set; }
}

public sealed class CinemaSettings
{
    public List<CinemaSpeaker> Speakers { get; set; } = [];
    public string? CalibrationMicId { get; set; }
}

/// <summary>Wave speaker mask order, not an assumed fixed channel position. Unassigned channels fold into fronts.</summary>
internal sealed class CinemaMatrix
{
    private static readonly uint[] Masks = [1, 2, 4, 8, 16, 32, 512, 1024];
    private readonly float[][] _rows;
    public int Channels { get; }
    public int Count => _rows.Length;
    public CinemaMatrix(int channels, uint mask, IReadOnlyList<CinemaSpeaker> speakers)
    {
        if (channels is not (6 or 8)) throw new ArgumentException("A discrete 5.1 or 7.1 source is required.");
        if (mask == 0) mask = channels == 6 ? 0x3fu : 0x63fu;
        if (BitOperations.PopCount(mask) != channels || (mask & ~0x63fu) != 0)
            throw new ArgumentException("Unsupported or inconsistent source speaker mask.");
        Validate(speakers);
        Channels = channels;
        _rows = speakers.Select(_ => new float[channels]).ToArray();
        int Index(CinemaRole role)
        {
            uint bit = Masks[(int)role];
            if ((mask & bit) == 0 && role == CinemaRole.RearLeft) bit = 512;
            if ((mask & bit) == 0 && role == CinemaRole.RearRight) bit = 1024;
            return (mask & bit) == 0 ? -1 : BitOperations.PopCount(mask & (bit - 1));
        }
        for (int i = 0; i < speakers.Count; i++)
        {
            int channel = Index(speakers[i].Role);
            if (channel >= 0) _rows[i][channel] = 1;
        }
        // Missing center and surrounds must not silently remove dialogue. Fold only when the physical role is absent.
        foreach (var role in Enum.GetValues<CinemaRole>())
        {
            int channel = Index(role);
            if (channel < 0 || _rows.Any(r => r[channel] != 0)) continue;
            for (int i = 0; i < speakers.Count; i++)
            {
                var front = speakers[i].Role;
                if (front == CinemaRole.FrontLeft && role is not (CinemaRole.RearRight or CinemaRole.SideRight) ||
                    front == CinemaRole.FrontRight && role is not (CinemaRole.RearLeft or CinemaRole.SideLeft))
                    _rows[i][channel] = role == CinemaRole.Subwoofer ? 0.5f : 0.70710678f;
            }
        }
        // Fixed headroom, never normalize according to current signal amplitude.
        foreach (var row in _rows)
        {
            float sum = row.Sum(Math.Abs);
            if (sum > 1) for (int c = 0; c < row.Length; c++) row[c] /= sum;
        }
    }
    public static void Validate(IReadOnlyList<CinemaSpeaker> speakers)
    {
        if (speakers.Count is < 2 or > 8 || !speakers.Any(s => s.Role == CinemaRole.FrontLeft) || !speakers.Any(s => s.Role == CinemaRole.FrontRight))
            throw new ArgumentException("Select at least distinct front-left and front-right speakers.");
        if (speakers.Select(s => s.Role).Distinct().Count() != speakers.Count ||
            speakers.Select(s => GroupPlan.Key(s.DeviceId)).Distinct().Count() != speakers.Count ||
            speakers.Any(s => string.IsNullOrWhiteSpace(s.DeviceId) || StereoPairs.IsPairId(s.DeviceId) || s.DelayMs is < 0 or > 500))
            throw new ArgumentException("Every physical speaker needs a unique role and a delay from 0 to 500 ms.");
    }
    public float Sample(int speaker, ReadOnlySpan<float> frame)
    {
        float value = 0;
        for (int c = 0; c < Channels; c++) value += frame[c] * _rows[speaker][c];
        return Math.Clamp(value, -1, 1);
    }
}

/// <summary>One multi-channel packet clock; every speaker is read from that exact packet before any RTP send.</summary>
public interface IGroupPacketSource : IDisposable
{
    bool ReadPacket(long sourceTicks);
    void ReadTarget(int index, Span<float> stereo);
    void SkipPackets(int packets);
}
