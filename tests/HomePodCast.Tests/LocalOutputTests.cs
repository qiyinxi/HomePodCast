using System.Collections.Concurrent;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.Tests;

internal sealed class FakeLocalRenderDevice : ILocalRenderDevice
{
    private readonly AutoResetEvent _ready = new(false);
    private readonly ConcurrentQueue<float[]> _writes = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly object _gate = new();
    private int _padding;
    private double _played;
    private long _ticks;
    public int Rate { get; init; } = 48000;
    public int BufferFrames => 1440;
    public int PeriodFrames => 480;
    public double StreamLatencyMs { get; init; } = 2;
    private bool _fail;
    public bool Disposed { get; private set; }
    public ConcurrentQueue<int> Threads { get; } = new();
    public int PaddingFrames { get { Check(); return _padding; } }
    public RenderPosition Position { get { Check(); return new(_played, _ticks); } }
    public void Start() => Check();
    public void WaitReady() { Check(); _ready.WaitOne(100); }
    public void Write(ReadOnlySpan<float> stereo)
    {
        Check();
        _padding += stereo.Length / 2;
        _writes.Enqueue(stereo.ToArray());
        _available.Release();
    }
    private void Check()
    {
        lock (_gate)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);
            if (_fail) throw new IOException("fake render device removed");
        }
    }
    public void Lose() { lock (_gate) { _fail = true; _ready.Set(); } }
    public void Advance(int frames, long ticks)
    {
        _padding = Math.Max(0, _padding - frames);
        _played += frames; _ticks = ticks;
        _ready.Set();
    }
    public async Task<float[]> NextWrite()
    {
        Assert.True(await _available.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(_writes.TryDequeue(out var samples));
        return samples;
    }
    public void Dispose() { Threads.Enqueue(Environment.CurrentManagedThreadId); Disposed = true; _ready.Dispose(); }
}

public class LocalOutputTests
{
    [Fact]
    public async Task Exact_endpoint_prepare_is_silent_and_volume_cap_mute_are_digital()
    {
        var device = new FakeLocalRenderDevice();
        double cap = 30;
        string? opened = null;
        using var sink = await LocalOutputSink.PrepareAsync(new("chosen-endpoint", "Bluetooth")
        {
            ExtraLatencyMs = 200, VolumeOffset = 10,
        }, () => "capture-endpoint", () => cap, default, id => { opened = id; return device; });
        Assert.Equal("chosen-endpoint", opened);
        Assert.Equal(OutputState.Preparing, sink.State);
        Assert.Equal(222, sink.MinLatencyMs);
        Assert.Equal(0, sink.Stats.PacketsSent);
        var timeline = new TimelineBuffer(44100, 44100, 0);
        timeline.WriteFrames(0, Enumerable.Repeat(1f, 44100 * 2).ToArray());
        sink.SetVolume(80);
        await sink.StartAsync(timeline, 300, default);
        Assert.Equal(OutputState.Streaming, sink.State);
        Assert.All(await device.NextWrite(), value => Assert.Equal(0, value));
        device.Advance(480, MediaClock.FromMs(500));
        Assert.All(await device.NextWrite(), value => Assert.InRange(value, 0.2999f, 0.3001f));
        cap = 20;
        device.Advance(480, MediaClock.FromMs(510));
        Assert.All(await device.NextWrite(), value => Assert.InRange(value, 0.1999f, 0.2001f));
        sink.SetVolume(80, mute: true);
        device.Advance(480, MediaClock.FromMs(520));
        Assert.All(await device.NextWrite(), value => Assert.Equal(0, value));
        sink.Dispose();
        Assert.True(device.Disposed);
        Assert.Equal(OutputState.Stopped, sink.State);
        Assert.Single(device.Threads.Distinct()); // open/use/dispose stay on the same render thread
    }

    [Fact]
    public async Task Capture_collision_is_refused_before_the_render_factory_opens()
    {
        int opens = 0;
        await Assert.ThrowsAsync<AirPlayException>(() => LocalOutputSink.PrepareAsync(new("ENDPOINT", "Local"),
            () => "endpoint", () => 10, default, _ => { opens++; return new FakeLocalRenderDevice(); }));
        Assert.Equal(0, opens);
        await Assert.ThrowsAsync<ArgumentException>(() => LocalOutputSink.PrepareAsync(new("", "Default is not allowed"),
            () => null, () => 10, default, _ => { opens++; return new FakeLocalRenderDevice(); }));
        Assert.Equal(0, opens);
    }

    [Fact]
    public async Task Shared_volume_is_read_for_each_render_block_without_a_stale_startup_snapshot()
    {
        var device = new FakeLocalRenderDevice();
        double master = 80;
        int reads = 0;
        using var sink = await LocalOutputSink.PrepareAsync(new("chosen", "Local"), () => "capture", () => 100,
            default, _ => device);
        sink.BindVolumeSource(() => { Interlocked.Increment(ref reads); return Volatile.Read(ref master); });
        var timeline = new TimelineBuffer(44100, 44100, 0);
        timeline.WriteFrames(0, Enumerable.Repeat(1f, 44100 * 2).ToArray());
        await sink.StartAsync(timeline, 200, default);
        await device.NextWrite();
        Assert.Equal(0, reads); // initialization never reads a value that could later overwrite a mute
        Volatile.Write(ref master, 0);
        device.Advance(480, MediaClock.FromMs(500));
        Assert.All(await device.NextWrite(), value => Assert.Equal(0, value));
        Volatile.Write(ref master, 80);
        device.Advance(480, MediaClock.FromMs(510));
        Assert.All(await device.NextWrite(), value => Assert.InRange(value, 0.7999f, 0.8001f));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task Switching_capture_onto_a_running_local_output_stops_before_another_write()
    {
        string? captured = "other";
        var device = new FakeLocalRenderDevice();
        using var sink = await LocalOutputSink.PrepareAsync(new("chosen", "Local"), () => Volatile.Read(ref captured),
            () => 10, default, _ => device);
        await sink.StartAsync(new TimelineBuffer(44100, 44100, 0), 100, default);
        await device.NextWrite();
        Volatile.Write(ref captured, "chosen");
        device.Advance(480, MediaClock.FromMs(500));
        Assert.Equal(L.T(LocalOutputSink.CapturedOutputText), await sink.Lost.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(OutputState.Lost, sink.State);
        Assert.Equal(0, sink.Stats.PacketsSent);
    }

    [Fact]
    public async Task Device_removal_reports_loss_and_disposes_the_selected_device()
    {
        var device = new FakeLocalRenderDevice();
        using var sink = await LocalOutputSink.PrepareAsync(new("chosen", "Local"), () => "other", () => 10, default, _ => device);
        await sink.StartAsync(new TimelineBuffer(44100, 44100, 0), 100, default);
        await device.NextWrite();
        device.Lose();
        Assert.Contains("removed", await sink.Lost.WaitAsync(TimeSpan.FromSeconds(5)));
        sink.Dispose();
        Assert.True(device.Disposed);
        Assert.Equal(OutputState.Stopped, sink.State);
    }

    [Theory]
    [InlineData(44100, -1000, -20)]
    [InlineData(44100, 1000, 20)]
    [InlineData(48000, -1000, 20)]
    [InlineData(48000, 1000, -20)]
    public void Marker_samples_leave_the_fake_device_at_group_time_within_one_ms_over_an_hour(int rate, int ppm, int trim)
    {
        const int groupDelay = 300, extraLatency = 200;
        double actualRate = rate * (1 + ppm * 1e-6);
        var timeline = new TimelineBuffer(44100, 44100, 0);
        var renderer = new LocalOutputRenderer(timeline, new("fake", "Bluetooth")
        {
            ExtraLatencyMs = extraLatency, TrimMs = trim,
        }, rate, 480, groupDelay, () => 100);
        renderer.SetVolume(100);
        var source = new float[640 * 2];
        var output = new float[480 * 2];
        for (int second = 1; second <= 3600; second += 11)
        {
            double played = second * actualRate;
            double written = played + 960;
            double firstHeardMs = second * 1000.0 + 960 * 1000 / actualRate + extraLatency;
            double firstSource = (firstHeardMs - groupDelay - trim) * 44100 / 1000;
            long begin = (long)Math.Floor(firstSource) - 64;
            long marker = (long)Math.Round(firstSource + 44100 * 0.005);
            Array.Clear(source);
            source[(marker - begin) * 2] = 1;
            source[(marker - begin) * 2 + 1] = -1;
            timeline.WriteFrames(begin, source);
            Assert.True(renderer.Render(written, new(played, MediaClock.FromMs(second * 1000.0)), output));
            int peak = Enumerable.Range(0, 480).MaxBy(i => Math.Abs(output[i * 2]));
            Assert.InRange(Math.Abs(output[peak * 2]), 0.5f, 1.01f);
            double heardMs = firstHeardMs + peak * 1000 / actualRate;
            double expectedMs = marker * 1000.0 / 44100 + groupDelay + trim;
            Assert.InRange(Math.Abs(heardMs - expectedMs), 0, 1);
        }
    }
}
