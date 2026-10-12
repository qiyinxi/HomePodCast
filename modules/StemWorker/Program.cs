using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

// Optional, separate process: no ONNX dependency or native DLL is loaded by the main app.
// stdin: sequence(int64), 1024 interleaved stereo float frames. stdout: same sequence - 128,
// 1024 estimated vocal frames. Protocol is binary; diagnostics only go to stderr.
const int hop = 128, frames = 1024;
const string modelHash = "235e795229ecfbb453a295302cad34d608d520f7fb336e2ed6e1dfd7f0a0e857";
try
{
    string path = Path.Combine(AppContext.BaseDirectory, "vocals.onnx");
    using (var file = File.OpenRead(path))
        if (!Convert.ToHexString(SHA256.HashData(file)).Equals(modelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsupported model or SHA-256 mismatch; install the pinned StemgenRT 0.7.0 module.");
    using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
    options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
    options.AddSessionConfigEntry("session.inter_op.allow_spinning", "0");
    using var session = new InferenceSession(path, options);
    string[] stateNames = ["vocals_audio_history", "vocals_local_hidden", "vocals_global_hidden", "vocals_spectral_numerator_tail"];
    int[][] shapes = [[1,2,896], [2,20,96], [2,1,192], [1,1,2,128]];
    void Check(string name, int[] shape, bool output = false)
    {
        var metadata = output ? session.OutputMetadata : session.InputMetadata;
        if (!metadata.TryGetValue(name, out var node) || node.ElementType != typeof(float) || !node.Dimensions.SequenceEqual(shape))
            throw new InvalidDataException($"Unexpected model tensor: {name}");
    }
    Check("audio_chunk", [1,2,hop]); Check("vocals_chunk", [1,1,2,hop], true);
    var audio = new float[2 * hop];
    var states = shapes.Select(s => new float[s.Aggregate(1, (n, x) => n * x)]).ToArray();
    var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor("audio_chunk", new DenseTensor<float>(audio, [1,2,hop])) };
    for (int i = 0; i < stateNames.Length; i++)
    {
        Check(stateNames[i], shapes[i]); Check("next_" + stateNames[i], shapes[i], true);
        inputs.Add(NamedOnnxValue.CreateFromTensor(stateNames[i], new DenseTensor<float>(states[i], shapes[i])));
    }
    void Infer(ReadOnlySpan<float> input, Span<float> vocals)
    {
        for (int f = 0; f < hop; f++) { audio[f] = input[f * 2]; audio[hop + f] = input[f * 2 + 1]; }
        using var output = session.Run(inputs);
        var separated = output.First(o => o.Name == "vocals_chunk").AsTensor<float>();
        for (int f = 0; f < hop; f++)
        {
            vocals[f * 2] = separated[0, 0, 0, f]; vocals[f * 2 + 1] = separated[0, 0, 1, f];
            if (!float.IsFinite(vocals[f * 2]) || !float.IsFinite(vocals[f * 2 + 1]))
                throw new InvalidDataException("Model produced non-finite audio.");
        }
        for (int i = 0; i < stateNames.Length; i++)
            output.First(o => o.Name == "next_" + stateNames[i]).AsTensor<float>().ToArray().CopyTo(states[i], 0);
    }
    var pcm = new float[frames * 2]; var result = new float[frames * 2];
    if (args.Contains("--verify"))
    {
        string fixture = args[Array.IndexOf(args, "--verify") + 1];
        using (var file = File.OpenRead(fixture))
            if (!Convert.ToHexString(SHA256.HashData(file)).Equals("c50a65c6e11d12c8cc475a2c13b4e5f7b26be03247a600b3619724cbe15778f8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Reference fixture SHA-256 mismatch.");
        using var reference = new BinaryReader(File.OpenRead(fixture));
        if (System.Text.Encoding.ASCII.GetString(reference.ReadBytes(8)) != "SGRTG001") throw new InvalidDataException("Fixture format mismatch.");
        int cases = reference.ReadInt32(); double maxError = 0;
        if (cases != 8) throw new InvalidDataException("Fixture case count mismatch.");
        for (int test = 0; test < cases; test++)
        {
            int length = reference.ReadInt32();
            if (length <= 0 || length > 20000) throw new InvalidDataException("Invalid fixture length.");
            var inputSamples = new float[length * 2]; var expectedSamples = new float[length * 8];
            for (int i = 0; i < inputSamples.Length; i++) inputSamples[i] = reference.ReadSingle();
            for (int i = 0; i < expectedSamples.Length; i++) expectedSamples[i] = reference.ReadSingle();
            foreach (var state in states) Array.Clear(state);
            int hops = (length + hop - 1) / hop;
            for (int h = 0; h <= hops; h++) // one zero-input EOF flush
            {
                for (int f = 0; f < hop; f++)
                    for (int ch = 0; ch < 2; ch++) pcm[f * 2 + ch] = h * hop + f < length ? inputSamples[ch * length + h * hop + f] : 0;
                Infer(pcm.AsSpan(0, hop * 2), result.AsSpan(0, hop * 2));
                if (h == 0) continue; // graph pre-roll
                for (int f = 0; f < hop && (h - 1) * hop + f < length; f++)
                    for (int ch = 0; ch < 2; ch++) maxError = Math.Max(maxError,
                        Math.Abs(result[f * 2 + ch] - expectedSamples[(4 + ch) * length + (h - 1) * hop + f]));
            }
        }
        if (maxError > 1e-5 || !double.IsFinite(maxError)) throw new InvalidDataException($"Reference parity failed: {maxError}");
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { reference = "independent PyTorch", cases, maxError, tolerance = 1e-5 }));
        return;
    }
    if (args.Contains("--benchmark"))
    {
        var times = new double[1000];
        for (int i = 0; i < 100; i++) Infer(pcm.AsSpan(0, hop * 2), result.AsSpan(0, hop * 2));
        for (int i = 0; i < times.Length; i++)
        {
            // Non-silent input avoids a misleading zero-only benchmark.
            for (int f = 0; f < hop; f++) pcm[f * 2] = pcm[f * 2 + 1] = (float)(0.2 * Math.Sin((i * hop + f) * 0.07));
            long start = Stopwatch.GetTimestamp();
            Infer(pcm.AsSpan(0, hop * 2), result.AsSpan(0, hop * 2));
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        double average = times.Average(); Array.Sort(times);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { model = "StemgenRT 0.7.0", hopMs = hop * 1000.0 / 44100,
            averageMs = average, p95Ms = times[950], maxMs = times[^1], realtimeRatio = hop * 1000.0 / 44100 / average }));
        return;
    }
    using var reader = new BinaryReader(Console.OpenStandardInput());
    using var writer = new BinaryWriter(Console.OpenStandardOutput());
    writer.Write(0x31535448); writer.Write(frames); writer.Write(hop); writer.Write(44100); writer.Flush();
    long expected = 0;
    while (true)
    {
        long sequence;
        try { sequence = reader.ReadInt64(); } catch (EndOfStreamException) { break; }
        if (sequence != expected) foreach (var state in states) Array.Clear(state);
        expected = sequence + frames;
        for (int i = 0; i < pcm.Length; i++) pcm[i] = reader.ReadSingle();
        for (int at = 0; at < frames; at += hop) Infer(pcm.AsSpan(at * 2, hop * 2), result.AsSpan(at * 2, hop * 2));
        writer.Write(sequence - hop);
        foreach (float sample in result) writer.Write(sample);
        writer.Flush();
    }
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
