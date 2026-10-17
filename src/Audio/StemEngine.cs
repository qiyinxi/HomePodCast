using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>A failure of the separation module, with the kind the UI explains (<see cref="StemError"/>).</summary>
internal sealed class StemModuleException(StemError kind, string message) : Exception(message)
{
    public StemError Kind { get; } = kind;
}

/// <summary>
/// What separates the blocks of <see cref="LiveStemMixer"/>'s pipeline: the optional module process
/// (<see cref="ProcessStemEngine"/>), or an in-process fake in the tests. Called on the pipeline's own thread.
/// </summary>
internal interface IStemEngine : IDisposable
{
    /// <summary>Blocks until blocks can be separated; throws <see cref="StemModuleException"/>.</summary>
    void Start();

    /// <summary>
    /// Separates one block of <see cref="LiveStemMixer.BlockFrames"/> interleaved stereo frames that starts at input frame
    /// <paramref name="first"/>: <paramref name="vocals"/> gets the estimated vocals of frames first − 128 onwards (the
    /// model answers one 128-frame hop late). Blocks; throws <see cref="StemModuleException"/>.
    /// </summary>
    void Separate(long first, ReadOnlySpan<float> input, Span<float> vocals);

    /// <summary>From another thread: makes a blocked <see cref="Start"/> or <see cref="Separate"/> return soon.</summary>
    void Abort();
}

/// <summary>
/// The optional module (modules/StemWorker): a separate process, so the app itself never loads ONNX Runtime or the
/// model. Binary protocol on stdin/stdout, diagnostics on stderr, exit code 2 when it refuses its model.
/// </summary>
internal sealed class ProcessStemEngine(string directory) : IStemEngine
{
    public const string ExeName = "HomePodCast.StemWorker.exe";

    /// <summary>The worker's exit code for a missing or unsupported model (modules/StemWorker/Program.cs).</summary>
    public const int ModelRejectedExitCode = 2;

    private const int Magic = 0x31535448, LoadTimeoutMs = 20_000, BlockTimeoutMs = 2000;
    private readonly byte[] _request = new byte[8 + LiveStemMixer.BlockFrames * 8];
    private readonly byte[] _response = new byte[8 + LiveStemMixer.BlockFrames * 8];
    private Process? _process;
    private Task<string>? _stderr;
    private System.Threading.Timer? _deadline;
    private volatile bool _timedOut, _aborted;

    public void Start()
    {
        var start = new ProcessStartInfo(Path.Combine(directory, ExeName))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory,
        };
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Process.Start returned nothing"); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            throw new StemModuleException(StemError.StartFailed, $"{ExeName} did not start: {ex.Message}");
        }
        _process = process;
        if (_aborted) { Kill(); throw new OperationCanceledException(); }
        // Drained concurrently: an error must never fill the pipe and deadlock inference. Shown in the log on failure.
        _stderr = process.StandardError.ReadToEndAsync();
        // Killing the child closes its pipe ends and unblocks a stalled synchronous read/write.
        _deadline = new System.Threading.Timer(_ => { _timedOut = true; Kill(); }, null, LoadTimeoutMs, Timeout.Infinite);
        var header = new byte[16];
        Read(header);
        _deadline.Change(Timeout.Infinite, Timeout.Infinite);
        if (BitConverter.ToInt32(header, 0) != Magic || BitConverter.ToInt32(header, 4) != LiveStemMixer.BlockFrames ||
            BitConverter.ToInt32(header, 8) != LiveStemMixer.Hop || BitConverter.ToInt32(header, 12) != 44100)
            throw new StemModuleException(StemError.Incompatible, $"unknown protocol header {Convert.ToHexString(header)}");
    }

    public void Separate(long first, ReadOnlySpan<float> input, Span<float> vocals)
    {
        BitConverter.TryWriteBytes(_request, first);
        MemoryMarshal.AsBytes(input).CopyTo(_request.AsSpan(8));
        _deadline!.Change(BlockTimeoutMs, Timeout.Infinite);
        try
        {
            var stdin = _process!.StandardInput.BaseStream;
            stdin.Write(_request);
            stdin.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { throw Failure(ex); }
        Read(_response);
        _deadline.Change(Timeout.Infinite, Timeout.Infinite);
        if (BitConverter.ToInt64(_response, 0) != first - LiveStemMixer.Hop)
            throw new StemModuleException(StemError.Failed, "the module's output lost its input alignment");
        MemoryMarshal.Cast<byte, float>(_response.AsSpan(8)).CopyTo(vocals);
    }

    private void Read(byte[] buffer)
    {
        try { _process!.StandardOutput.BaseStream.ReadExactly(buffer); }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ObjectDisposedException) { throw Failure(ex); }
    }

    /// <summary>Why the pipe broke: our timeout, or the module's exit code and what it wrote to stderr.</summary>
    private Exception Failure(Exception broken)
    {
        if (_aborted) return new OperationCanceledException();
        if (_timedOut) return new StemModuleException(StemError.TimedOut, "the module did not answer in time");
        var process = _process!;
        int? code = null;
        try { if (process.WaitForExit(1000)) code = process.ExitCode; } catch (Exception) { }
        string stderr = "";
        try { if (_stderr is { } task && task.Wait(1000)) stderr = task.Result.Trim(); } catch (Exception) { }
        return new StemModuleException(code == ModelRejectedExitCode ? StemError.ModelRejected : StemError.Failed,
            $"exit code {code?.ToString() ?? "none"}: {(stderr.Length > 0 ? stderr : broken.Message)}");
    }

    public void Abort()
    {
        _aborted = true;
        Kill();
    }

    private void Kill()
    {
        try { if (_process is { HasExited: false } p) p.Kill(true); } catch (Exception) { }
    }

    public void Dispose()
    {
        Kill();
        _deadline?.Dispose();
        _process?.Dispose();
    }
}
