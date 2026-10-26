using System.Diagnostics;
using System.Text.Json;
using HomePodCast;
using HomePodCast.Audio;
using HomePodCast.Net;
using Microsoft.Win32;

#if DEBUG
throw new InvalidOperationException("Resource measurements require a Release test host");
#endif
if (args.FirstOrDefault() == "--ab")
{
    try { MixerControllerBenchmark.Run(args.Length > 1 ? args[1] : "test-results/mixer-controller-ab.json"); }
    catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    return;
}
int seconds = args.Length > 0 ? int.Parse(args[0]) : 60;
if (seconds < 60) throw new ArgumentException("Measure at least 60 seconds after warmup");
string output = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.GetFullPath("test-results/mixer-resources.json");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
var results = new List<object>();
foreach (bool compressed in new[] { false, true })
foreach (int destinations in new[] { 1, 2, 4 })
{
    using var hub = new MixerCaptureHub(44100, t => new FakeInput(t.RootPid * .001f));
    var consumers = Enumerable.Range(0, destinations).Select(i => new Consumer(i, compressed,
        Enumerable.Range(1, 8).Where(root => (root - 1) % destinations == i).Sum(root => root * .001f))).ToArray();
    var captures = consumers.Select(c => hub.Subscribe(c.Id.ToString(), c.Fifo)).ToArray();
    foreach (var capture in captures) capture.Start();
    hub.Apply(new(1, Enumerable.Range(1, 8).Select(i => new MixerRoot(new((uint)i, "root" + i, false),
        ((i - 1) % destinations).ToString())).ToArray()));
    void Tick() { hub.Cycle(); foreach (var consumer in consumers) consumer.Read(); }
    for (int i = 0; i < 300; i++) Tick();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    var process = Process.GetCurrentProcess(); process.Refresh();
    long beforePrivate = process.PrivateMemorySize64, peakPrivate = beforePrivate;
    int threads = process.Threads.Count;
    var cpu = process.TotalProcessorTime;
    long allocation = GC.GetTotalAllocatedBytes(true), threadAllocation = GC.GetAllocatedBytesForCurrentThread();
    var watch = Stopwatch.StartNew();
    var lateness = new List<double>(seconds * 100 + 1);
    long cycles = 0;
    while (watch.Elapsed.TotalSeconds < seconds)
    {
        double due = cycles * .01, remaining = due - watch.Elapsed.TotalSeconds;
        if (remaining > .001) Thread.Sleep(Math.Max(1, (int)(remaining * 1000)));
        while (watch.Elapsed.TotalSeconds < due) Thread.SpinWait(10);
        lateness.Add(Math.Max(0, (watch.Elapsed.TotalSeconds - due) * 1000));
        Tick(); cycles++;
        if (cycles % 100 == 0) { process.Refresh(); peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64); }
    }
    watch.Stop(); process.Refresh();
    double cpuSeconds = (process.TotalProcessorTime - cpu).TotalSeconds;
    long allocated = GC.GetTotalAllocatedBytes(true) - allocation;
    long captureThreadAllocated = GC.GetAllocatedBytesForCurrentThread() - threadAllocation;
    lateness.Sort();
    foreach (var consumer in consumers) consumer.Verify();
    long dropped = consumers.Sum(c => c.Fifo.DroppedFrames);
    foreach (var capture in captures) capture.Dispose();
    if (hub.InputCount != 0 || hub.BusCount != 0) throw new InvalidOperationException("Capture lease leak");
    foreach (var consumer in consumers) consumer.Dispose();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); process.Refresh();
    var row = new { destinations, mode = compressed ? "mixed-AAC-MP3" : "PCM", roots = 8, rate = 44100,
        membersPerDestination = compressed ? 2 : 1, delayMs = compressed ? 2000 : 105, seconds = watch.Elapsed.TotalSeconds,
        cpuSeconds, cpuCores = cpuSeconds / watch.Elapsed.TotalSeconds, allocatedBytes = allocated,
        captureThreadAllocatedBytes = captureThreadAllocated, privateBytesBefore = beforePrivate,
        peakPrivateBytes = peakPrivate, privateBytesAfterDispose = process.PrivateMemorySize64,
        threadsBefore = threads, threadsAfter = process.Threads.Count, dropped,
        lateP50Ms = lateness[lateness.Count / 2], lateP99Ms = lateness[(int)(lateness.Count * .99)], lateMaxMs = lateness[^1],
        sampleChecks = consumers.Sum(c => c.CheckedFrames), encodedBytes = consumers.Sum(c => c.EncodedBytes) };
    results.Add(row);
    File.WriteAllText(output, JsonSerializer.Serialize(new { cpu = cpuKey?.GetValue("ProcessorNameString"),
        nominalMHz = cpuKey?.GetValue("~MHz"), logicalProcessors = Environment.ProcessorCount,
        runtime = Environment.Version.ToString(), host = "Release console; in-process input/render/DLNA consumers; no discovery or audio endpoints",
        results }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(row));
}

sealed class FakeInput(float marker) : IMixerProcessInput
{
    private readonly float[] _samples = Enumerable.Repeat(marker, 882).ToArray();
    public bool Drain(List<float> samples) { samples.AddRange(_samples); return true; }
    public void Dispose() { }
}

sealed class Consumer : IDisposable
{
    public int Id { get; }
    public AudioFifo Fifo { get; } = new(44100, 16, 58);
    public long CheckedFrames, EncodedBytes;
    private readonly float[] _samples = new float[882];
    private readonly float[] _render = new float[882];
    private readonly float _marker;
    private readonly TimelineBuffer? _history;
    private readonly IDlnaAudioEncoder? _aac, _mp3;
    private readonly short[] _aacInput, _mp3Input;
    private int _aacFrames, _mp3Frames;
    private long _frame;
    private readonly EffectChain _effects = new(new Compressor(44100) { Enabled = false });
    public Consumer(int id, bool compressed, float marker)
    {
        Id = id; _marker = marker;
        if (compressed)
        {
            _history = new(44100, 5 * 44100, 0);
            _aac = DlnaAudioEncoders.Create(DlnaAudioFormat.Aac); _mp3 = DlnaAudioEncoders.Create(DlnaAudioFormat.Mp3);
        }
        _aacInput = new short[(_aac?.FramesPerInput ?? 1) * 2];
        _mp3Input = new short[(_mp3?.FramesPerInput ?? 1) * 2];
    }
    public void Read()
    {
        if (!Fifo.Read(_samples)) return;
        foreach (float sample in _samples)
            if (Math.Abs(sample - _marker) > .0001) throw new InvalidDataException($"Destination {Id} marker mismatch: {sample} vs {_marker}");
        CheckedFrames += 441;
        _effects.Process(_samples, 441);
        if (_history != null)
        {
            _history.WriteFrames(_frame, _samples);
            if (!_history.ReadFrames(_frame, _render) || !_render.SequenceEqual(_samples)) throw new InvalidDataException("Fake render data mismatch");
            _frame += 441;
        }
        if (_aac != null) Encode(_aac, _aacInput, ref _aacFrames);
        if (_mp3 != null) Encode(_mp3, _mp3Input, ref _mp3Frames);
    }
    private void Encode(IDlnaAudioEncoder encoder, short[] input, ref int frames)
    {
        for (int f = 0; f < 441; f++)
        {
            input[frames * 2] = (short)Math.Clamp(_samples[2 * f] * 32767, short.MinValue, short.MaxValue);
            input[frames * 2 + 1] = (short)Math.Clamp(_samples[2 * f + 1] * 32767, short.MinValue, short.MaxValue);
            if (++frames != encoder.FramesPerInput) continue;
            foreach (var packet in encoder.Encode(input))
            { if (packet.Data.Length == 0 || packet.PcmFrames <= 0) throw new InvalidDataException("Empty encoder packet"); EncodedBytes += packet.Data.Length; }
            frames = 0;
        }
    }
    public void Verify()
    {
        if (CheckedFrames < 60 * 44100) throw new InvalidDataException("The fake consumer did not read a full minute");
        if (_aac != null && EncodedBytes == 0) throw new InvalidDataException("No encoded output");
    }
    public void Dispose() { _aac?.Dispose(); _mp3?.Dispose(); }
}
