namespace HomePodCast.Audio;

/// <summary>
/// When the stream last carried sound: the last packet at the end of the send chain (what the speaker plays, the mic
/// included) with a sample above <see cref="Threshold"/>. 夜间自动断开 (NightDisconnectPolicy) waits for this and the
/// keyboard and mouse to be idle. While nothing streams nothing passes, so the time stays at the stream's last sound.
/// Times are <see cref="Environment.TickCount64"/> milliseconds, which, like the keyboard's and mouse's last-input time,
/// go on through sleep.
/// </summary>
public sealed class SoundActivity : IAudioEffect
{
    /// <summary>-60 dBFS: far above the zeros of a paused player, far below anything anyone listens to.</summary>
    public const float Threshold = 0.001f;

    private const long Never = long.MinValue;
    private long _lastAudibleMs = Never;
    private readonly Func<long> _clock;

    public SoundActivity(Func<long>? clock = null) => _clock = clock ?? (() => Environment.TickCount64);

    public void Process(Span<float> interleaved, int frames)
    {
        var samples = interleaved[..Math.Min(interleaved.Length, frames * 2)];
        foreach (float s in samples)
        {
            if (s > Threshold || s < -Threshold)
            {
                Volatile.Write(ref _lastAudibleMs, _clock());
                return;
            }
        }
    }

    /// <summary>How long since the last audible packet, at <paramref name="nowMs"/>; <see cref="TimeSpan.MaxValue"/>: none in this run.</summary>
    public TimeSpan SilentFor(long nowMs)
    {
        long last = Volatile.Read(ref _lastAudibleMs);
        return last == Never ? TimeSpan.MaxValue : TimeSpan.FromMilliseconds(Math.Max(0, nowMs - last));
    }
}
