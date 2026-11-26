using HomePodCast.Audio;

namespace HomePodCast.UI;

// The adaptive FIFO's learned target outlives the app (AppConfig.FifoLearnedTargetMs), kept per captured output device:
// every controller that plays captured sound starts its connections from it (StreamController.RestoreFifoTarget) and
// what it has learned since is put back here. 2026-11-26: the target lived in memory only, so each start of the app (an
// update, a restart) began at the configured 16 ms and a virtual sound card that needs 50 ms replayed its dropouts.
internal sealed partial class TrayApp
{
    /// <summary>While streaming, how often the learned target is looked at (saved only when it changed: a dropout, or 1 ms relaxed in 10 minutes).</summary>
    private const int FifoMemorySaveMs = 60_000;

    private System.Windows.Forms.Timer? _fifoMemoryTimer;

    /// <summary>Called once from the constructor (UI thread), before the first connect.</summary>
    private void InitFifoMemory()
    {
        UseFifoMemory(Controller);
        WatchFifoTarget(Controller);
        // What a crash or a kill leaves behind; a session's end and Quit save without waiting for it.
        _fifoMemoryTimer = new System.Windows.Forms.Timer { Interval = FifoMemorySaveMs };
        _fifoMemoryTimer.Tick += (_, _) =>
        {
            if (!_quitting) SaveFifoTargets();
        };
        _fifoMemoryTimer.Start();
    }

    /// <summary>
    /// The startup controller and every one the mixer makes for a destination: each has its own FIFO, fed from the same
    /// captured device (by the capture itself, or through the shared hub). The 5.1 FIFO has a fixed target and no part in it.
    /// </summary>
    private void UseFifoMemory(StreamController controller)
    {
        // The endpoint whose sound is captured, as the controller's CapturedOutputId: the chosen device, else the default
        // output. Its late packets are what the target was learned from, so another device never inherits it.
        controller.FifoMemoryKey = () => CaptureEndpoint.Captured(Routing.CaptureDeviceId, _knownDefaultOutputId);
        // On the stream's thread: the dictionary is replaced, never changed in place (AppConfig.RememberFifoTarget).
        controller.RememberedFifoTargetMs = id => Config.FifoLearnedTargetFor(id);
    }

    /// <summary>
    /// A controller's session ended (disconnected, lost, released while the Apple TV is idle): what its FIFO learned is
    /// saved now, read from this controller, which the mixer may already have let go of when the post runs.
    /// </summary>
    private void WatchFifoTarget(StreamController controller)
    {
        bool streaming = false;
        controller.Changed += () => _ui.Post(_ =>
        {
            bool now = controller.State == StreamState.Streaming;
            if (_quitting || now == streaming) return;
            streaming = now;
            if (!now) SaveFifoTargets(controller);
        }, null);
    }

    /// <summary>UI thread: the config gets what the streaming controllers (and one whose session just ended) learned; saved when that changed it.</summary>
    private void SaveFifoTargets(StreamController? ended = null)
    {
        var controllers = (Mixer?.Controllers ?? []).Append(_legacyController).OfType<StreamController>()
            .Where(c => c.State == StreamState.Streaming);
        if (ended != null) controllers = controllers.Append(ended);
        if (RememberFifoTargets(Config, controllers.Distinct())) Config.TrySave();
    }

    /// <summary>
    /// Puts each capture device's learned target into the config; true when that changed it. Several destinations
    /// streaming at once capture the same device with a FIFO each: the largest target is the one to start from.
    /// </summary>
    internal static bool RememberFifoTargets(AppConfig config, IEnumerable<StreamController> controllers)
    {
        var learned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in controllers)
            if (controller.LearnedFifoTarget is { } target)
                learned[target.Key] = Math.Max(learned.GetValueOrDefault(target.Key), target.TargetMs);
        bool changed = false;
        foreach (var (captureDeviceId, ms) in learned) changed |= config.RememberFifoTarget(captureDeviceId, ms);
        return changed;
    }

    /// <summary>Quit, before the controllers stop: the sessions still streaming are the ones whose targets count.</summary>
    private void DisposeFifoMemory()
    {
        _fifoMemoryTimer?.Dispose();
        try { SaveFifoTargets(); }
        catch (Exception ex) { Log.Warn($"saving the learned FIFO target: {ex.Message}"); }
    }
}
