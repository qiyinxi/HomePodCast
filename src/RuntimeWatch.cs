using System.Runtime.ExceptionServices;
using Timer = System.Threading.Timer;

namespace HomePodCast;

/// <summary>
/// Notices that the shared .NET runtime this copy started on has been uninstalled since (<see cref="AppEnvironment.RuntimeGone"/>):
/// a .NET update replaced it while the app ran. What is loaded goes on working, but whatever the process uses for the
/// first time from then on fails until it is restarted. 2026-10-13: an SDK install replaced runtime 10.0.12 with
/// 10.0.13 on the maintainer's PC, and the next 「检查更新」 failed with FileNotFoundException for System.Net.Http; the
/// first stream after such an update can fail the same way.
///
/// The check is one File.Exists, never on the UI thread: every <see cref="Interval"/>, after a wake from sleep
/// (<see cref="CheckSoon"/>), and whenever an assembly could not be loaded (a first-chance FileNotFoundException or
/// FileLoadException that names one, whoever catches it). Once the runtime is gone the watch stops and calls
/// <c>removed</c>, once. A self-contained copy (the Microsoft Store package) has its runtime in its own folder, which
/// nothing removes under it: <see cref="For"/> gives no watch for it.
/// </summary>
internal sealed class RuntimeWatch : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly string _folder;
    private readonly Func<string, bool> _fileExists;
    private readonly Action _removed;
    private readonly TimeSpan _interval;
    private readonly object _lock = new();
    private readonly HashSet<string> _failedLoads = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _watchingExceptions;
    private int _stopped; // 1 once the runtime was found gone, or the watch disposed

    private RuntimeWatch(string folder, Func<string, bool> fileExists, Action removed, TimeSpan interval)
    {
        _folder = folder;
        _fileExists = fileExists;
        _removed = removed;
        _interval = interval;
    }

    /// <summary>A watch for <paramref name="runtimeFolder"/>, or null when it is not a shared runtime (self-contained: nothing to watch).</summary>
    /// <param name="fileExists">File.Exists (tests: a fake).</param>
    /// <param name="removed">Called once, on a thread-pool thread, when the runtime is found gone.</param>
    public static RuntimeWatch? For(string runtimeFolder, Func<string, bool> fileExists, Action removed, TimeSpan? interval = null) =>
        AppEnvironment.IsSharedRuntime(runtimeFolder) ? new RuntimeWatch(runtimeFolder, fileExists, removed, interval ?? Interval) : null;

    /// <summary>The runtime was found gone (the watch has stopped).</summary>
    public bool Removed { get; private set; }

    /// <summary>
    /// Starts the timer and, with <paramref name="watchExceptions"/>, the look at every exception thrown in this process
    /// (the app; tests call <see cref="Saw"/> instead). Checks once right away, which also gets the check's own code
    /// ready while everything it needs is still there.
    /// </summary>
    public void Start(bool watchExceptions = true)
    {
        lock (_lock)
        {
            if (_stopped != 0 || _timer != null) return;
            _timer = new Timer(_ => Check("by the periodic check"), null, _interval, _interval);
            if (watchExceptions)
            {
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
                _watchingExceptions = true;
            }
        }
        CheckSoon("at start");
    }

    /// <summary>Checks on the thread pool (after a wake from sleep, at the start).</summary>
    /// <param name="why">For the log line: "after a wake from sleep".</param>
    public void CheckSoon(string why)
    {
        if (Volatile.Read(ref _stopped) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => Check(why));
    }

    /// <summary>The check itself, on the calling thread: true when the runtime is gone (then the watch stops and calls <c>removed</c>, once).</summary>
    internal bool Check(string why)
    {
        if (Volatile.Read(ref _stopped) != 0) return Removed;
        bool gone;
        try { gone = AppEnvironment.RuntimeGone(_folder, _fileExists); }
        catch (Exception) { return false; } // a file system hiccup: the next check decides
        if (!gone || Interlocked.Exchange(ref _stopped, 1) != 0) return gone;
        Removed = true;
        StopWatching();
        Log.Warn($"runtime: the .NET runtime this copy started on ({_folder.TrimEnd('\\', '/')}) has been removed by a .NET update " +
                 $"(found {why}); whatever HomePodCast uses for the first time from now on can fail until it restarts");
        _removed();
        return true;
    }

    private void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e) => Saw(e.Exception);

    /// <summary>
    /// An exception was thrown somewhere. One for an assembly that could not be loaded is logged (once per assembly) and
    /// starts a check; everything else is ignored at the cost of a type test. Runs on the thread that threw: never throws,
    /// never waits.
    /// </summary>
    internal void Saw(Exception ex)
    {
        if (Volatile.Read(ref _stopped) != 0 || MissingAssembly(ex) is not { } assembly) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            bool first;
            lock (_lock) first = _failedLoads.Add(assembly);
            var failure = Update.UpdateFlow.Describe(ex);
            if (!Check($"after {failure}") && first)
                Log.Info($"runtime: an assembly could not be loaded ({failure}), but the runtime this copy started on is still there");
        });
    }

    /// <summary>
    /// The assembly a FileNotFoundException or FileLoadException could not load (its display name, "System.Net.Http,
    /// Version=…", or a .dll path); null for any other exception, also for a missing file that is no assembly.
    /// </summary>
    internal static string? MissingAssembly(Exception ex)
    {
        string? name = ex switch
        {
            FileNotFoundException f => f.FileName,
            FileLoadException f => f.FileName,
            _ => null,
        };
        return name != null && (name.Contains(", Version=", StringComparison.Ordinal) || name.Contains("PublicKeyToken=", StringComparison.Ordinal)
                                || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ? name : null;
    }

    private void StopWatching()
    {
        lock (_lock)
        {
            _timer?.Dispose();
            _timer = null;
            if (_watchingExceptions) AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            _watchingExceptions = false;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stopped, 1);
        StopWatching();
    }
}
