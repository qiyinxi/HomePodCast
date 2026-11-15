using HomePodCast.Audio;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

/// <summary>
/// The AAC encoder's types: the third-party stereo stream unchanged, and the HomePod's 48 kHz 5.1 (experimental since
/// 1.4). Encoded in memory only; nothing plays and no audio device opens.
/// </summary>
public sealed class AacMultichannelTests(ITestOutputHelper output)
{
    [Fact]
    public void StereoEncoderKeepsItsTypes()
    {
        using var encoder = new AacEncoder();
        Assert.Equal(2, encoder.ChannelCount);
        Assert.Equal(44100, encoder.SampleRate);
        Assert.Equal("2 ch, 44100 Hz, 24000 B/s, profile 0x29", encoder.Settings);
        Assert.Equal(new byte[] { 0x12, 0x10 }, encoder.AudioSpecificConfig()); // AAC-LC, 44.1 kHz, stereo
        Assert.Throws<ArgumentException>(() => encoder.Encode(new short[1024 * 6]));
    }

    [Fact]
    public void SurroundEncoderMakesChannelConfiguration6OrIsReportedUnavailable()
    {
        AacEncoder encoder;
        try { encoder = new AacEncoder(6, 48000); }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            // A runner (or Windows N without the Media Feature Pack) may refuse 6-channel types: the app then streams stereo.
            output.WriteLine($"6-channel AAC unavailable here: {ex.Message}");
            Assert.Equal(L.T("Windows 的 AAC 编码器不支持 5.1"), AacEncoder.SurroundProblem());
            return;
        }
        using (encoder)
        {
            output.WriteLine(encoder.Settings);
            Assert.Contains("channel mask 0x3F", encoder.Settings);
            Assert.Equal(new byte[] { 0x11, 0xb0 }, encoder.AudioSpecificConfig()); // AAC-LC, 48 kHz, channelConfiguration 6
            Assert.Throws<ArgumentException>(() => encoder.Encode(new short[2048]));
            var pcm = new short[1024 * 6];
            var units = new List<byte[]>();
            for (int block = 0; block < 8; block++)
            {
                for (int i = 0; i < 1024; i++)
                    for (int c = 0; c < 6; c++)
                        pcm[i * 6 + c] = (short)(3000 * Math.Sin((block * 1024 + i) * 2 * Math.PI * (220 + 110 * c) / 48000));
                units.AddRange(encoder.Encode(pcm));
            }
            Assert.InRange(units.Count, 4, 8);
            Assert.All(units, u => Assert.InRange(u.Length, 4, 8192));
            Assert.All(units, u => Assert.False(u[0] == 0xff && (u[1] & 0xf0) == 0xf0)); // raw, no ADTS
        }
        Assert.Null(AacEncoder.SurroundProblem());
    }

    [Fact]
    public void SevenPointOneAndOtherLayoutsAreRefused()
    {
        var error = Assert.Throws<NotSupportedException>(() => new AacEncoder(8, 48000));
        Assert.Contains("7.1", error.Message);
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoder(4, 48000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AacEncoder(6, 32000));
    }
}
