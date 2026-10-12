using HomePodCast.Audio;

namespace HomePodCast.Tests;

public class CinemaTests
{
    private static List<CinemaSpeaker> Layout(int count = 6) => Enumerable.Range(0, count)
        .Select(i => new CinemaSpeaker { Role = (CinemaRole)i, DeviceId = $"00:00:00:00:00:{i + 1:00}" }).ToList();

    [Fact]
    public void Discrete_channels_reach_only_their_assigned_speaker()
    {
        var matrix = new CinemaMatrix(6, 0x3f, Layout());
        for (int channel = 0; channel < 6; channel++)
        {
            float[] frame = new float[6]; frame[channel] = 0.2f;
            for (int speaker = 0; speaker < 6; speaker++) Assert.Equal(channel == speaker ? 0.2f : 0, matrix.Sample(speaker, frame));
        }
    }
    [Fact]
    public void Side_mask_51_maps_to_the_surround_positions_and_missing_dialogue_folds_to_fronts()
    {
        var matrix = new CinemaMatrix(6, 0x60f, Layout());
        Assert.Equal(0.4f, matrix.Sample(4, [0,0,0,0,0.4f,0]));
        var fronts = new CinemaMatrix(6, 0x3f, Layout(2));
        Assert.True(fronts.Sample(0, [0,0,0.3f,0,0,0]) > 0);
        Assert.True(fronts.Sample(1, [0,0,0.3f,0,0,0]) > 0);
        Assert.InRange(fronts.Sample(0, [1,1,1,1,1,1]), 0.99f, 1f);
    }
    [Fact]
    public void Invalid_layout_and_stereo_input_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new CinemaMatrix(2, 3, Layout()));
        Assert.Throws<ArgumentException>(() => new CinemaMatrix(6, 3, Layout()));
        var duplicate = Layout(); duplicate[1].DeviceId = duplicate[0].DeviceId;
        Assert.Throws<ArgumentException>(() => CinemaMatrix.Validate(duplicate));
        Assert.Throws<ArgumentException>(() => CinemaMatrix.Validate(Layout(1)));
        duplicate = Layout(); duplicate[0].DelayMs = 501;
        Assert.Throws<ArgumentException>(() => CinemaMatrix.Validate(duplicate));
    }
    [Fact]
    public void Chirp_correlation_recovers_delay_with_noise_and_inverted_polarity()
    {
        var chirp = AcousticAlignment.Chirp(); var recording = new float[44100];
        var random = new Random(314);
        for (int i = 0; i < recording.Length; i++) recording[i] = (float)((random.NextDouble() - 0.5) * 0.005);
        const int delay = 11024;
        for (int i = 0; i < chirp.Length; i++) recording[delay + i] -= chirp[i] * 0.5f;
        var measurement = AcousticAlignment.Measure(recording);
        Assert.NotNull(measurement);
        Assert.InRange(measurement.Value.DelayMs, 249.8, 250.2);
        Assert.True(measurement.Value.Confidence > 0.9);
        Assert.Null(AcousticAlignment.Measure(new float[44100]));
    }
    [Fact]
    public void Only_reliable_complete_measurements_produce_relative_delays()
    {
        Assert.Equal([50, 0, 25], AcousticAlignment.RelativeOffsets([new(150,.9),new(200,.9),new(175,.9)]));
        Assert.Throws<ArgumentException>(() => AcousticAlignment.RelativeOffsets([new(150,.2),new(200,.9)]));
        Assert.Throws<ArgumentException>(() => AcousticAlignment.RelativeOffsets([new(0,.9),new(600,.9)]));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Native_decoder_preserves_surround_channels_from_a_local_wave(int bits)
    {
        string file = Path.Combine(Path.GetTempPath(), "hpc-cinema-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWave(file, bits);
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var decoder = new CinemaMediaReader(file);
                    Assert.Equal(6, decoder.Channels); Assert.Equal(48000, decoder.Rate);
                    var samples = decoder.Read(); Assert.NotNull(samples);
                    Assert.True(samples.Length >= 6);
                    for (int c = 0; c < 6; c++) Assert.InRange(samples[c], (c + 1) * 0.02f - 0.0001f, (c + 1) * 0.02f + 0.0001f);
                }
                catch (Exception ex) { error = ex; }
            });
            thread.SetApartmentState(ApartmentState.MTA); thread.Start();
            Assert.True(thread.Join(10000)); Assert.Null(error);
        }
        finally { File.Delete(file); }
    }
    internal static void WriteWave(string file, int bits, int frames = 4800)
    {
        using var writer = new BinaryWriter(File.Create(file));
        int size = frames * 6 * bits / 8;
        writer.Write("RIFF"u8); writer.Write(size + 60); writer.Write("WAVEfmt "u8); writer.Write(40);
        writer.Write((ushort)0xfffe); writer.Write((ushort)6); writer.Write(48000); writer.Write(48000 * 6 * bits / 8);
        writer.Write((ushort)(6 * bits / 8)); writer.Write((ushort)bits); writer.Write((ushort)22); writer.Write((ushort)bits);
        writer.Write(0x3f); writer.Write(new Guid(bits == 32 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        writer.Write("data"u8); writer.Write(size);
        for (int f = 0; f < frames; f++) for (int c = 0; c < 6; c++)
        { if (bits == 32) writer.Write((c + 1) * 0.02f); else writer.Write((short)((c + 1) * 0.02f * 32767)); }
    }
}
