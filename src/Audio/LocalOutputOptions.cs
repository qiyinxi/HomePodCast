using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>An explicitly chosen Windows render endpoint; Bluetooth link latency is additional to WASAPI's latency.</summary>
public sealed record LocalOutputOptions(string DeviceId, string Name)
{
    /// <summary>User classification only: changing this never replaces a measured link latency.</summary>
    public bool IsBluetooth { get; init; }
    public int ExtraLatencyMs { get; init; }
    public int TrimMs { get; init; }
    public int VolumeOffset { get; init; }
    public bool Required { get; init; }
    public ChannelMode Channels { get; init; }

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(DeviceId);
        if (ExtraLatencyMs is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(ExtraLatencyMs));
        if (TrimMs is < -5000 or > 5000) throw new ArgumentOutOfRangeException(nameof(TrimMs));
        if (VolumeOffset is < -100 or > 100) throw new ArgumentOutOfRangeException(nameof(VolumeOffset));
        if (!Enum.IsDefined(Channels)) throw new ArgumentOutOfRangeException(nameof(Channels));
    }
}

internal readonly record struct RenderPosition(double Frames, long QpcTicks)
{
    // IAudioClock's position is not necessarily in frames, and its correlated QPC is in 100 ns units.
    public static RenderPosition FromNative(ulong position, ulong frequency, ulong qpc100ns, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfZero(frequency);
        return new(position * (double)sampleRate / frequency,
            checked((long)((UInt128)qpc100ns * (ulong)MediaClock.Frequency / 10_000_000)));
    }
}

/// <summary>Correlates the endpoint clock with QPC; repeated snapshots prevent device drift accumulating as delay.</summary>
internal sealed class RenderClock(int sampleRate)
{
    private RenderPosition? _rateAnchor;
    public double Rate { get; private set; } = sampleRate;

    public long PresentationTicks(double writtenFrames, RenderPosition position, int extraLatencyMs)
    {
        if (_rateAnchor is not { } anchor || position.Frames < anchor.Frames || position.QpcTicks < anchor.QpcTicks)
        {
            _rateAnchor = position;
            Rate = sampleRate;
        }
        else if (position.QpcTicks - anchor.QpcTicks >= MediaClock.FromMs(500))
        {
            double measured = (position.Frames - anchor.Frames) * MediaClock.Frequency / (position.QpcTicks - anchor.QpcTicks);
            // Ignore stalled clocks and implausible readings instead of turning one driver glitch into a pitch jump.
            if (measured >= sampleRate * 0.98 && measured <= sampleRate * 1.02)
                Rate += 0.25 * (measured - Rate);
            _rateAnchor = position;
        }
        // Written minus played includes audio already handed to the engine. Adding GetStreamLatency again here
        // would count that pipeline twice. The Bluetooth radio's unreported link delay is separate.
        double queuedFrames = Math.Max(0, writtenFrames - position.Frames);
        return checked(position.QpcTicks + (long)Math.Round(queuedFrames * MediaClock.Frequency / Rate) + MediaClock.FromMs(extraLatencyMs));
    }
}
