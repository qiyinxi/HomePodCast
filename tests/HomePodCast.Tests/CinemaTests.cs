using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// The wireless cinema's channel matrix (which source channel each HomePod plays, and how loud), the acoustic
/// correlation, and Windows' own decoder on generated WAVEFORMATEXTENSIBLE files. Nothing plays and no microphone opens.
/// </summary>
public class CinemaTests
{
    [Fact]
    public void Full_stereo_targets_keep_left_and_right_and_share_the_existing_downmix()
    {
        var speakers = new[] { new CinemaSpeaker { DeviceId = "AA:01", Role = CinemaRole.Stereo },
            new CinemaSpeaker { DeviceId = "AA:02", Role = CinemaRole.Stereo } };
        var stereo = new CinemaMatrix(2, Stereo, speakers);
        Assert.Equal((0.25f, -0.5f), stereo.StereoSample(0, [0.25f, -0.5f]));
        Assert.Equal((0.25f, -0.5f), stereo.StereoSample(1, [0.25f, -0.5f]));
        foreach (var mask in new[] { Rear51, Side51, Surround71 })
        {
            int channels = System.Numerics.BitOperations.PopCount(mask);
            var full = new CinemaMatrix(channels, mask, speakers);
            var fronts = new CinemaMatrix(channels, mask, Layout(L2));
            Assert.Equal(fronts.Scale, full.Scale);
            for (int c = 0; c < channels; c++)
            {
                var frame = new float[channels]; frame[c] = 1;
                Assert.Equal((fronts.Sample(0, frame), fronts.Sample(1, frame)), full.StereoSample(0, frame));
            }
        }
    }

    private static readonly Dictionary<string, CinemaRole> Roles = new()
    {
        ["FL"] = CinemaRole.FrontLeft, ["FR"] = CinemaRole.FrontRight, ["C"] = CinemaRole.Center, ["Sub"] = CinemaRole.Subwoofer,
        ["BL"] = CinemaRole.RearLeft, ["BR"] = CinemaRole.RearRight, ["SL"] = CinemaRole.SideLeft, ["SR"] = CinemaRole.SideRight,
    };

    /// <summary>Speakers in the given positions ("FL FR C SL SR"), one HomePod each.</summary>
    internal static List<CinemaSpeaker> Layout(string roles) => roles.Split(' ', StringSplitOptions.RemoveEmptyEntries)
        .Select((r, i) => new CinemaSpeaker { Role = Roles[r], DeviceId = $"00:00:00:00:00:{i + 1:00}", Name = r }).ToList();

    public const string L2 = "FL FR", L3 = "FL FR C", L5 = "FL FR C SL SR", L5Rear = "FL FR C BL BR", L6 = "FL FR C SL SR Sub",
        L7 = "FL FR C SL SR BL BR", L8 = "FL FR C Sub BL BR SL SR";
    public const uint Stereo = 0x3, Mono = 0x4, Rear51 = 0x3f, Side51 = 0x60f, Surround71 = 0x63f;

    /// <summary>
    /// What each speaker plays before the headroom scale: "FL + C-3 + LFE-6" is the source's front left at full level,
    /// its center at −3 dB and its LFE at −6 dB; "" is silence. Source channels: FL FR C LFE BL BR SL SR.
    /// </summary>
    public static TheoryData<string, uint, string, string> Routing => new()
    {
        // 2.0: the front pair only, whatever the room has; nothing is upmixed.
        { "2.0 on 2", Stereo, L2, "FL=FL | FR=FR" },
        { "2.0 on 3", Stereo, L3, "FL=FL | FR=FR | C=" },
        { "2.0 on 5", Stereo, L5, "FL=FL | FR=FR | C= | SL= | SR=" },
        { "2.0 on 6", Stereo, L6, "FL=FL | FR=FR | C= | SL= | SR= | Sub=" },
        { "2.0 on 7", Stereo, L7, "FL=FL | FR=FR | C= | SL= | SR= | BL= | BR=" },
        // Mono is the center channel.
        { "mono on 2", Mono, L2, "FL=C-3 | FR=C-3" },
        { "mono on 3", Mono, L3, "FL= | FR= | C=C" },
        // 5.1 with rear surrounds (0x3f, the usual WAV/AAC mask).
        { "5.1 rear on 2", Rear51, L2, "FL=FL + C-3 + LFE-6 + BL-3 | FR=FR + C-3 + LFE-6 + BR-3" },
        { "5.1 rear on 3", Rear51, L3, "FL=FL + LFE-6 + BL-3 | FR=FR + LFE-6 + BR-3 | C=C" },
        { "5.1 rear on 5 (side speakers)", Rear51, L5, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=BL | SR=BR" },
        { "5.1 rear on 5 (rear speakers)", Rear51, L5Rear, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | BL=BL | BR=BR" },
        { "5.1 rear on 6", Rear51, L6, "FL=FL | FR=FR | C=C | SL=BL | SR=BR | Sub=LFE" },
        { "5.1 rear on 7", Rear51, L7, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=BL-3 | SR=BR-3 | BL=BL-3 | BR=BR-3" },
        // 5.1 with side surrounds (0x60f).
        { "5.1 side on 2", Side51, L2, "FL=FL + C-3 + LFE-6 + SL-3 | FR=FR + C-3 + LFE-6 + SR-3" },
        { "5.1 side on 3", Side51, L3, "FL=FL + LFE-6 + SL-3 | FR=FR + LFE-6 + SR-3 | C=C" },
        { "5.1 side on 5 (side speakers)", Side51, L5, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=SL | SR=SR" },
        { "5.1 side on 5 (rear speakers)", Side51, L5Rear, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | BL=SL | BR=SR" },
        { "5.1 side on 6", Side51, L6, "FL=FL | FR=FR | C=C | SL=SL | SR=SR | Sub=LFE" },
        { "5.1 side on 7", Side51, L7, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=SL-3 | SR=SR-3 | BL=SL-3 | BR=SR-3" },
        // 7.1 (0x63f): up to seven HomePods plus one for the LFE; side and rear fold together where one pair is missing.
        { "7.1 on 2", Surround71, L2, "FL=FL + C-3 + LFE-6 + BL-3 + SL-3 | FR=FR + C-3 + LFE-6 + BR-3 + SR-3" },
        { "7.1 on 3", Surround71, L3, "FL=FL + LFE-6 + BL-3 + SL-3 | FR=FR + LFE-6 + BR-3 + SR-3 | C=C" },
        { "7.1 on 5 (side speakers)", Surround71, L5, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=SL + BL-3 | SR=SR + BR-3" },
        { "7.1 on 5 (rear speakers)", Surround71, L5Rear, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | BL=BL + SL-3 | BR=BR + SR-3" },
        { "7.1 on 6", Surround71, L6, "FL=FL | FR=FR | C=C | SL=SL + BL-3 | SR=SR + BR-3 | Sub=LFE" },
        { "7.1 on 7", Surround71, L7, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=SL | SR=SR | BL=BL | BR=BR" },
        { "7.1 on 8", Surround71, L8, "FL=FL | FR=FR | C=C | Sub=LFE | BL=BL | BR=BR | SL=SL | SR=SR" },
        // Rarer masks: the old 7.1 (front-of-center pair, 0xff) and 6.1 (back center, 0x70f).
        { "7.1 wide on 3", 0xff, L3, "FL=FL + LFE-6 + BL-3 + FLC-3 | FR=FR + LFE-6 + BR-3 + FRC-3 | C=C + FLC-3 + FRC-3" },
        { "6.1 on 5", 0x70f, L5, "FL=FL + LFE-6 | FR=FR + LFE-6 | C=C | SL=SL + BC-3 | SR=SR + BC-3" },
        { "6.1 on 2", 0x70f, L2, "FL=FL + C-3 + LFE-6 + BC-6 + SL-3 | FR=FR + C-3 + LFE-6 + BC-6 + SR-3" },
    };

    [Theory]
    [MemberData(nameof(Routing))]
    public void Every_source_channel_reaches_the_right_speakers(string what, uint mask, string layout, string expected)
    {
        var speakers = Layout(layout);
        int channels = System.Numerics.BitOperations.PopCount(mask);
        var matrix = new CinemaMatrix(channels, mask, speakers);
        var positions = CinemaChannels.Positions(mask).Select(CinemaChannels.Short).ToList();

        // The expected gains, and the one headroom factor that keeps the busiest speaker from clipping.
        var rows = expected.Split('|').Select(r => r.Trim().Split('=')).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(speakers.Select(s => s.Name), rows.Keys);
        var gains = rows.ToDictionary(r => r.Key, r =>
        {
            var row = new float[channels];
            foreach (var term in r.Value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = term.Split('-');
                row[positions.IndexOf(parts[0])] += parts.Length == 1 ? 1 : parts[1] == "3" ? 0.70710678f : 0.5f;
            }
            return row;
        });
        float scale = 1 / Math.Max(1, gains.Values.Max(r => r.Sum()));
        Assert.Equal(scale, (double)matrix.Scale, 5);
        for (int s = 0; s < speakers.Count; s++)
            for (int c = 0; c < channels; c++)
                Assert.True(Math.Abs(gains[speakers[s].Name][c] * scale - matrix.Gain(s, c)) < 1e-5,
                    $"{what}: {speakers[s].Name} gets {positions[c]} at {matrix.Gain(s, c):F4}, expected {gains[speakers[s].Name][c] * scale:F4} ({matrix.Describe()})");
    }

    /// <summary>The headroom column of docs/WIRELESS_CINEMA.md's 7.1 table.</summary>
    [Theory]
    [InlineData(L2, -11.2)]
    [InlineData(L3, -9.3)]
    [InlineData(L5, -4.6)]
    [InlineData(L6, -4.6)]
    [InlineData(L7, -3.5)]
    [InlineData(L8, 0.0)]
    public void A_71_file_needs_the_documented_headroom(string layout, double db) =>
        Assert.Equal(db, Math.Round(20 * Math.Log10(new CinemaMatrix(8, Surround71, Layout(layout)).Scale), 1));

    [Fact]
    public void Every_channel_of_a_full_layout_is_discrete_and_needs_no_headroom()
    {
        foreach (var (mask, layout) in new[] { (Rear51, "FL FR C Sub BL BR"), (Side51, L6), (Surround71, L8) })
        {
            var speakers = Layout(layout);
            int channels = System.Numerics.BitOperations.PopCount(mask);
            var matrix = new CinemaMatrix(channels, mask, speakers);
            Assert.Equal(1, matrix.Scale);
            for (int channel = 0; channel < channels; channel++)
            {
                float[] frame = new float[channels];
                frame[channel] = 0.2f;
                var hearing = Enumerable.Range(0, speakers.Count).Where(s => matrix.Sample(s, frame) != 0).ToList();
                Assert.Equal(0.2f, matrix.Sample(Assert.Single(hearing), frame));
            }
        }
    }

    [Fact]
    public void A_full_scale_signal_on_every_channel_does_not_clip()
    {
        foreach (var layout in new[] { L2, L3, L5, L6, L7, L8 })
            foreach (uint mask in new[] { Stereo, Rear51, Side51, Surround71 })
            {
                int channels = System.Numerics.BitOperations.PopCount(mask);
                var matrix = new CinemaMatrix(channels, mask, Layout(layout));
                var frame = Enumerable.Repeat(1f, channels).ToArray();
                for (int s = 0; s < matrix.Count; s++) Assert.InRange(matrix.Sample(s, frame), 0, 1.00001f);
            }
    }

    [Theory]
    [InlineData(6, 0u, 0x3fu)]
    [InlineData(8, 0u, 0x63fu)]
    [InlineData(2, 0u, 0x3u)]
    [InlineData(1, 0u, 0x4u)]
    [InlineData(7, 0u, 0x70fu)]
    [InlineData(6, 0x60fu, 0x60fu)]
    [InlineData(8, 0xffu, 0xffu)]
    public void A_decoders_mask_or_the_usual_one_for_its_channel_count(int channels, uint mask, uint expected) =>
        Assert.Equal(expected, CinemaChannels.Resolve(channels, mask));

    [Theory]
    [InlineData(6, 0x63fu)]   // 8 positions named for 6 channels
    [InlineData(8, 0x3fu)]    // 6 for 8
    [InlineData(10, 0u)]      // more than 7.1
    [InlineData(8, 0xa3fu)]   // a top speaker (0x800) among the eight
    public void Layouts_it_cannot_place_are_refused_with_a_message_in_the_ui_language(int channels, uint mask)
    {
        Assert.Null(CinemaChannels.Resolve(channels, mask));
        var ex = Assert.Throws<CinemaException>(() => new CinemaMatrix(channels, mask, Layout(L2)));
        Assert.Equal("这个文件的声道布局不支持：只能播放单声道、立体声、5.1 和 7.1 等最多 8 个声道的标准布局。", ex.Message);
        Assert.Contains("unsupported channel layout", ex.LogText);
    }

    [Fact]
    public void Invalid_speaker_layouts_are_refused_with_a_message_in_the_ui_language()
    {
        Assert.Equal("至少要给前左和前右各选一台音箱。",
            Assert.Throws<CinemaException>(() => CinemaMatrix.Validate(Layout("FL C"))).Message);
        Assert.Equal("至少要给前左和前右各选一台音箱。",
            Assert.Throws<CinemaException>(() => CinemaMatrix.Validate(Layout("FL"))).Message);
        var duplicate = Layout(L5);
        duplicate[1].DeviceId = duplicate[0].DeviceId;
        Assert.Equal("每个位置只能选一台音箱，一台音箱也只能放在一个位置。",
            Assert.Throws<CinemaException>(() => CinemaMatrix.Validate(duplicate)).Message);
        var pair = Layout(L2);
        pair[0].DeviceId = Net.StereoPairs.PairId("6A1B2C3D-0000-4000-8000-0123456789AB");
        Assert.Equal("立体声对不能用在无线影院里，请选单独的 HomePod。",
            Assert.Throws<CinemaException>(() => CinemaMatrix.Validate(pair)).Message);
        var late = Layout(L2);
        late[0].DelayMs = 501;
        Assert.Equal("音箱延迟要在 0 到 500 毫秒之间。", Assert.Throws<CinemaException>(() => CinemaMatrix.Validate(late)).Message);
    }

    [Fact]
    public void The_log_says_what_each_speaker_plays()
    {
        var matrix = new CinemaMatrix(8, Surround71, Layout(L5));
        Assert.Equal("FrontLeft=FL+LFE-6dB, FrontRight=FR+LFE-6dB, Center=C, SideLeft=BL-3dB+SL, SideRight=BR-3dB+SR, headroom -4.6 dB",
            matrix.Describe());
        Assert.Equal("7.1 (0x63f)", CinemaChannels.Describe(8, Surround71));
        Assert.Equal("5.1 (side) (0x60f)", CinemaChannels.Describe(6, Side51));
    }

    [Theory]
    [InlineData(6, 48000, 0x3fu, 6, 48000, 0x3fu, "Same")]
    [InlineData(6, 48000, 0x3fu, 6, 48000, 0u, "Same")]       // no mask now: the usual one, unchanged
    [InlineData(6, 48000, 0x3fu, 6, 48000, 0x60fu, "NewMask")]
    [InlineData(6, 48000, 0x3fu, 8, 48000, 0x63fu, "Unsupported")]
    [InlineData(6, 48000, 0x3fu, 6, 44100, 0x3fu, "Unsupported")]
    [InlineData(6, 48000, 0x3fu, 6, 48000, 0x63fu, "Unsupported")]
    public void A_format_change_mid_file_goes_on_only_with_the_same_channels_and_rate(int channels, int rate, uint mask,
        int newChannels, int newRate, uint newMask, string expected) =>
        Assert.Equal(expected, CinemaMediaReader.Follow(channels, rate, mask, newChannels, newRate, newMask).ToString());

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
        Assert.Equal([50, 0, 25], AcousticAlignment.RelativeOffsets([new(150, .9), new(200, .9), new(175, .9)]));
        Assert.Equal("有的音箱没有测到可靠的结果，原来的延迟没有改。",
            Assert.Throws<CinemaException>(() => AcousticAlignment.RelativeOffsets([new(150, .2), new(200, .9)])).Message);
        Assert.Equal("各音箱测到的时间相差超过 500 毫秒，结果不可靠，原来的延迟没有改。",
            Assert.Throws<CinemaException>(() => AcousticAlignment.RelativeOffsets([new(0, .9), new(600, .9)])).Message);
        Assert.Equal(25, AcousticAlignment.MeasurementVolumeCapPercent);
    }

    // ---------------------------------------------------------------- Windows' decoder on generated files

    public static TheoryData<int, int, uint, int> Files => new()
    {
        { 16, 6, Rear51, 48000 },
        { 32, 6, Side51, 48000 },
        { 16, 8, Surround71, 48000 },
        { 32, 8, Surround71, 44100 },
        { 16, 2, Stereo, 48000 },
        { 16, 1, Mono, 44100 },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void Native_decoder_keeps_every_channel_and_the_mask_of_a_local_wave(int bits, int channels, uint mask, int rate)
    {
        string file = TempWave(bits, channels, mask, rate);
        try
        {
            OnMta(() =>
            {
                using var decoder = new CinemaMediaReader(file);
                Assert.Equal(channels, decoder.Channels);
                Assert.Equal(rate, decoder.Rate);
                Assert.Equal(mask, CinemaChannels.Resolve(decoder.Channels, decoder.Mask));
                var samples = decoder.Read();
                Assert.NotNull(samples);
                Assert.True(samples.Length >= channels);
                for (int c = 0; c < channels; c++) Assert.InRange(samples[c], Level(c) - 0.0001f, Level(c) + 0.0001f);
            });
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void A_missing_file_or_one_without_a_decoder_is_refused_with_a_message_in_the_ui_language()
    {
        string garbage = Path.Combine(Path.GetTempPath(), "hpc-cinema-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray());
        try
        {
            OnMta(() =>
            {
                Assert.Equal("选的文件不存在或打不开。",
                    Assert.Throws<CinemaException>(() => new CinemaMediaReader(garbage + ".missing")).Message);
                Assert.Equal("选的文件不存在或打不开。",
                    Assert.Throws<CinemaException>(() => new CinemaMediaReader("https://example.invalid/film.mp4")).Message);
                Assert.Equal("这个文件在这台电脑上无法解码：Windows 没有它的解码器，或者文件已损坏。",
                    Assert.Throws<CinemaException>(() => new CinemaMediaReader(garbage)).Message);
            });
        }
        finally { File.Delete(garbage); }
    }

    /// <summary>The level each channel of a generated file has: 0.02, 0.04, … (channel order).</summary>
    internal static float Level(int channel) => (channel + 1) * 0.02f;

    internal static string TempWave(int bits, int channels, uint mask, int rate, int frames = 4800, Func<int, int, float>? sample = null)
    {
        string file = Path.Combine(Path.GetTempPath(), "hpc-cinema-" + Guid.NewGuid().ToString("N") + ".wav");
        WriteWave(file, bits, channels, mask, rate, frames, sample);
        return file;
    }

    /// <summary>A WAVEFORMATEXTENSIBLE file: 16-bit PCM or 32-bit float, the given speaker mask.</summary>
    internal static void WriteWave(string file, int bits, int channels = 6, uint mask = Rear51, int rate = 48000, int frames = 4800,
        Func<int, int, float>? sample = null)
    {
        sample ??= (_, c) => Level(c);
        using var writer = new BinaryWriter(File.Create(file));
        int block = channels * bits / 8, size = frames * block;
        writer.Write("RIFF"u8); writer.Write(size + 60); writer.Write("WAVEfmt "u8); writer.Write(40);
        writer.Write((ushort)0xfffe); writer.Write((ushort)channels); writer.Write(rate); writer.Write(rate * block);
        writer.Write((ushort)block); writer.Write((ushort)bits); writer.Write((ushort)22); writer.Write((ushort)bits);
        writer.Write(mask);
        writer.Write(new Guid(bits == 32 ? "00000003-0000-0010-8000-00aa00389b71" : "00000001-0000-0010-8000-00aa00389b71").ToByteArray());
        writer.Write("data"u8); writer.Write(size);
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
            {
                float v = sample(f, c);
                if (bits == 32) writer.Write(v); else writer.Write((short)Math.Round(v * 32767));
            }
    }

    /// <summary>Media Foundation runs on an MTA thread, as the cinema's decoder thread does.</summary>
    internal static void OnMta(Action body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(20000), "the decoder test did not finish");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }
}
