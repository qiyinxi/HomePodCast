using System.Buffers.Binary;
using System.Text.Json;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.ProtocolHarness;

// Windows produces these bytes with the production codec. Linux only replays them; no capture or audio endpoint exists.
internal sealed class DlnaCodecFixture
{
    internal sealed record Packet(int Offset, int Length, int PcmFrames);
    internal sealed record Manifest(string Format, int SampleRate, int Channels, int FramesPerInput,
        int InputFrames, List<Packet[]> EncodeCalls, Packet[] DrainPackets);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public const int SampleRate = 44_100;
    public const int SourceFrames = SampleRate * 6;
    public const int WarmupFrames = SampleRate / 4;
    private readonly Manifest _manifest;
    private readonly byte[] _media, _source;
    private readonly DlnaAudioFormat _format;
    public int FramesPerInput => _manifest.FramesPerInput;
    public int InputFrames => _manifest.InputFrames;
    public long OutputPcmFrames => _manifest.EncodeCalls.SelectMany(p => p).Concat(_manifest.DrainPackets).Sum(p => (long)p.PcmFrames);

    private DlnaCodecFixture(DlnaAudioFormat format, Manifest manifest, byte[] media, byte[] source)
        => (_format, _manifest, _media, _source) = (format, manifest, media, source);

    // Smooth, different channels remain codec friendly. A nonrepeating chirp and amplitude envelope prevent a
    // repeated sine from hiding a dropped block. The opening chirp is also a unique time-alignment marker.
    public static short Signal(long frame, int channel)
    {
        if (frame < WarmupFrames || frame >= SourceFrames - SampleRate / 4) return 0;
        double t = (frame - WarmupFrames) / (double)SampleRate;
        double fade = Math.Min(1, Math.Min(t * 100, (5.5 - t) * 100));
        double envelope = 0.72 + 0.18 * Math.Sin(2 * Math.PI * (0.37 * t + 0.091 * t * t));
        double phase = channel == 0 ? 2 * Math.PI * (317 * t + 93 * t * t + 1.7 * t * t * t)
            : 2 * Math.PI * (641 * t + 57 * t * t + 2.3 * t * t * t) + 0.71;
        double second = channel == 0 ? Math.Sin(2 * Math.PI * (1091 * t + 13 * t * t))
            : Math.Sin(2 * Math.PI * (1573 * t + 17 * t * t) + 1.31);
        return (short)Math.Round(fade * envelope * (12_000 * Math.Sin(phase) + 2_700 * second));
    }

    public static void Generate(string directory)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("Real DLNA codec fixtures require Windows GitHub Actions; no audio endpoint is opened.");
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return; // Preserve the platform guard inside the thread delegate as well.
            try
            {
                foreach (var format in new[] { DlnaAudioFormat.Aac, DlnaAudioFormat.Mp3 })
                {
                    string target = Path.Combine(directory, DlnaAudioFormats.Extension(format));
                    Directory.CreateDirectory(target);
                    using var encoder = new DlnaMediaFoundationEncoder(format);
                    using var media = File.Create(Path.Combine(target, "signal." + DlnaAudioFormats.Extension(format)));
                    using var source = new BinaryWriter(File.Create(Path.Combine(target, "source.pcm")));
                    var calls = new List<Packet[]>();
                    int inputFrames = ((SourceFrames + encoder.FramesPerInput - 1) / encoder.FramesPerInput) * encoder.FramesPerInput;
                    var pcm = new short[encoder.FramesPerInput * 2];
                    Packet[] Save(IReadOnlyList<DlnaEncodedPacket> packets)
                    {
                        var entries = new List<Packet>();
                        foreach (var packet in packets)
                        {
                            entries.Add(new(checked((int)media.Position), packet.Data.Length, packet.PcmFrames));
                            media.Write(packet.Data);
                        }
                        return entries.ToArray();
                    }
                    for (int frame = 0; frame < inputFrames; frame += encoder.FramesPerInput)
                    {
                        for (int i = 0; i < encoder.FramesPerInput; i++)
                            for (int ch = 0; ch < 2; ch++)
                            {
                                short value = Signal(frame + i, ch);
                                pcm[i * 2 + ch] = value;
                                source.Write(value);
                            }
                        calls.Add(Save(encoder.Encode(pcm)));
                    }
                    Packet[] drain = Save(encoder.Drain());
                    var manifest = new Manifest(format.ToString(), SampleRate, 2, encoder.FramesPerInput,
                        inputFrames, calls, drain);
                    File.WriteAllText(Path.Combine(target, "manifest.json"), JsonSerializer.Serialize(manifest, Json));
                    Console.WriteLine($"DLNA {format}: {inputFrames} source frames; {media.Length} encoded bytes; "
                        + $"Encode calls={calls.Count}, zero-output={calls.Count(p => p.Length == 0)}, "
                        + $"multi-output={calls.Count(p => p.Length > 1)}, drain packets={drain.Length}, "
                        + $"packet PCM duration={calls.SelectMany(p => p).Concat(drain).Sum(p => (long)p.PcmFrames)} frames. "
                        + "Production Media Foundation codec, dedicated MTA, no audio endpoint.");
                }
            }
            catch (Exception e) { failure = e; }
        }) { Name = "DLNA CI codec fixtures", IsBackground = true };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2))) throw new TimeoutException("DLNA fixture encoding exceeded two minutes.");
        if (failure is not null) throw new InvalidOperationException("DLNA fixture generation failed.", failure);
    }

    public static DlnaCodecFixture Load(string directory, DlnaAudioFormat format)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))
            ?? throw new InvalidDataException("Missing codec fixture manifest.");
        byte[] media = File.ReadAllBytes(Path.Combine(directory, "signal." + DlnaAudioFormats.Extension(format)));
        byte[] source = File.ReadAllBytes(Path.Combine(directory, "source.pcm"));
        int block = format == DlnaAudioFormat.Aac ? 1024 : 1152;
        if (manifest.Format != format.ToString() || manifest.SampleRate != SampleRate || manifest.Channels != 2
            || manifest.FramesPerInput != block || manifest.InputFrames < SourceFrames || manifest.InputFrames > SourceFrames + block
            || manifest.InputFrames != manifest.EncodeCalls.Count * block || source.Length != manifest.InputFrames * 4
            || media.Length is < 100 or > 1_000_000)
            throw new InvalidDataException("The codec fixture format, duration or bounds are invalid.");
        int position = 0;
        foreach (var packet in manifest.EncodeCalls.SelectMany(p => p).Concat(manifest.DrainPackets))
        {
            if (packet.Offset != position || packet.Length < 0 || packet.Length > 1_000_000
                || packet.PcmFrames < 0 || packet.Length > 0 && packet.PcmFrames <= 0)
                throw new InvalidDataException("Invalid codec packet offset/duration.");
            position = checked(position + packet.Length);
        }
        if (position != media.Length) throw new InvalidDataException("Fixture media does not match its packet map.");
        return new(format, manifest, media, source);
    }

    public IDlnaAudioEncoder CreateEncoder(DlnaAudioFormat format)
        => format == _format ? new ReplayEncoder(this) : throw new InvalidOperationException("Wrong fixture codec selected.");

    private sealed class ReplayEncoder(DlnaCodecFixture fixture) : IDlnaAudioEncoder
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private int _call;
        public int FramesPerInput => fixture.FramesPerInput;
        private DlnaEncodedPacket[] Read(Packet[] packets) => packets.Select(p =>
            new DlnaEncodedPacket(fixture._media.AsSpan(p.Offset, p.Length).ToArray(), p.PcmFrames)).ToArray();
        private void RequireOwner()
        {
            if (_owner != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Fixture codec left its owner thread.");
        }
        public IReadOnlyList<DlnaEncodedPacket> Encode(ReadOnlySpan<short> stereo)
        {
            RequireOwner();
            if (stereo.Length != FramesPerInput * 2) throw new InvalidDataException("Production server did not reblock the PCM callback.");
            if (_call == fixture._manifest.EncodeCalls.Count)
            {
                // This is a finite six-second fixture in a live HTTP session. Keep the connection alive with no
                // packets while the renderer drains its queued PCM; an empty result must never terminate chunking.
                if (stereo.ContainsAnyExcept((short)0)) throw new InvalidDataException("Unexpected PCM beyond the fixture.");
                return [];
            }
            int start = _call * FramesPerInput * 4;
            for (int i = 0; i < stereo.Length; i++)
                if (stereo[i] != BinaryPrimitives.ReadInt16LittleEndian(fixture._source.AsSpan(start + i * 2)))
                    throw new InvalidDataException($"PCM reblocking/gain/channel mismatch in codec input call {_call}, sample {i}.");
            return Read(fixture._manifest.EncodeCalls[_call++]);
        }
        public IReadOnlyList<DlnaEncodedPacket> Drain() { RequireOwner(); return Read(fixture._manifest.DrainPackets); }
        public void Dispose() => RequireOwner();
    }
}
