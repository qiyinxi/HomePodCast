using HomePodCast.Net;

namespace HomePodCast.Audio;

/// <summary>Pure render step, also used by the simulated device tests. Gain is digital, never endpoint volume.</summary>
internal sealed class LocalOutputRenderer(TimelineBuffer timeline, LocalOutputOptions options, int sampleRate,
    int maxFrames, int groupDelayMs, Func<double> cap, Func<double>? volumeSource = null)
{
    private readonly RenderClock _clock = new(sampleRate);
    private readonly ClockedTimelineReader _reader = new(timeline, sampleRate, maxFrames);
    private double _volume;
    public double DriftPpm => (_clock.Rate / sampleRate - 1) * 1_000_000;

    public void SetVolume(double percent, bool mute = false) => Volatile.Write(ref _volume, mute ? 0 : percent);

    public bool Render(double writtenFrames, RenderPosition position, Span<float> stereo)
    {
        long presentation = _clock.PresentationTicks(writtenFrames, position, options.ExtraLatencyMs);
        bool complete = _reader.Read(presentation, groupDelayMs, options.TrimMs, _clock.Rate, stereo);
        double volume = volumeSource?.Invoke() ?? Volatile.Read(ref _volume);
        float gain = (float)(volume <= 0 || double.IsNaN(volume) ? 0 : VolumeLimit.Clamp(volume + options.VolumeOffset, cap()) / 100);
        for (int frame = 0; frame < stereo.Length / 2; frame++)
        {
            float left = stereo[frame * 2], right = stereo[frame * 2 + 1];
            if (options.Channels == ChannelMode.LeftOnly) right = left;
            else if (options.Channels == ChannelMode.RightOnly) left = right;
            stereo[frame * 2] = float.IsFinite(left) ? Math.Clamp(left * gain, -1, 1) : 0;
            stereo[frame * 2 + 1] = float.IsFinite(right) ? Math.Clamp(right * gain, -1, 1) : 0;
        }
        return complete;
    }
}
