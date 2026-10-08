using HomePodCast.Audio;
using HomePodCast.Net;
using Xunit;

namespace HomePodCast.Tests;

public class MixInTests
{
    [Fact]
    public void A_silent_mic_leaves_the_stream_untouched_and_allocates_nothing()
    {
        var tap = new AudioTap(RtpSender.SampleRate, RtpSender.SampleRate, targetMs: 20, capMs: 80);
        var mix = new MixIn(tap);
        var pcm = new float[RtpSender.FramesPerPacket * 2];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = MathF.Sin(i * 0.01f) * 0.5f;
        var expected = (float[])pcm.Clone();

        mix.Process(pcm, RtpSender.FramesPerPacket); // warm up
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int k = 0; k < 100; k++) mix.Process(pcm, RtpSender.FramesPerPacket);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, pcm);
        Assert.Equal(0, allocated);
    }
}
