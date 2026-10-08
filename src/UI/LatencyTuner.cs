namespace HomePodCast.UI;

/// <summary>A restartable one-shot delay (a WinForms timer in the app, a manual one in tests).</summary>
internal interface IDebounce
{
    bool Enabled { get; }
    void Restart();
    void Stop();
    event Action? Tick;
}

/// <summary>System.Windows.Forms.Timer as an <see cref="IDebounce"/>; ticks keep coming until stopped.</summary>
internal sealed class TimerDebounce : IDebounce, IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;

    public TimerDebounce(int intervalMs)
    {
        _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        _timer.Tick += (_, _) => Tick?.Invoke();
    }

    public bool Enabled => _timer.Enabled;
    public event Action? Tick;

    public void Restart()
    {
        _timer.Stop();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Dispose();
}

/// <summary>
/// The rules of the scene buttons and the latency slider (首页). Latency is negotiated at SETUP, so a
/// change means a reconnect; it is applied only once things are still:
/// <list type="bullet">
/// <item>the slider (drag, keys, − / +) waits until the mouse is released and nothing moved for 1 s;</item>
/// <item>a scene switch waits until no scene change for 1 s, so cycling through scenes reconnects once;</item>
/// <item>moving the slider by hand while a preset is active switches to 自定义 and supersedes a pending scene;</item>
/// <item>values below the speaker's floor (<see cref="Floor"/>) can't be chosen.</item>
/// </list>
/// </summary>
internal sealed class LatencyTuner
{
    public const int MaxMs = 500, SmallStep = 1, LargeStep = 10;

    private readonly AppConfig _config;
    private readonly Action<int> _apply;
    private readonly Action _save;
    private readonly IDebounce _slider, _scene;
    private int _pendingSceneMs;
    private int _floor;

    /// <param name="apply">Called with the latency to use (TrayApp.SetLatency: saves, reconnects if connected).</param>
    /// <param name="save">Saves the config (scene changes); defaults to AppConfig.Save.</param>
    public LatencyTuner(AppConfig config, Action<int> apply, IDebounce slider, IDebounce scene, int floor, Action? save = null)
    {
        _config = config;
        _apply = apply;
        _save = save ?? config.Save;
        _slider = slider;
        _scene = scene;
        _floor = Math.Min(floor, MaxMs);
        Value = Clamp(config.LatencyMs);
        _slider.Tick += OnSliderTick;
        _scene.Tick += OnSceneTick;
    }

    /// <summary>Whether the mouse still holds the slider.</summary>
    public Func<bool> IsHeld { get; set; } = () => false;

    /// <summary>Latency the slider shows (ms).</summary>
    public int Value { get; private set; }

    public Scene Scene => _config.Scene;

    /// <summary>A change waits for its reconnect.</summary>
    public bool Pending => _slider.Enabled || _scene.Enabled;

    /// <summary>Raised after Value or the scene changed (the UI redraws).</summary>
    public event Action? Changed;

    /// <summary>Lowest selectable latency: the speaker's processing time plus the safety margin.</summary>
    public int Floor
    {
        get => _floor;
        set
        {
            value = Math.Min(value, MaxMs);
            if (value == _floor) return;
            _floor = value;
            int shown = Clamp(Value);
            if (shown == Value) return;
            Value = shown; // display only: the controller applies the same floor when it connects
            Changed?.Invoke();
        }
    }

    public int Clamp(int ms) => Math.Clamp(ms, _floor, MaxMs);

    /// <summary>The user moved the slider (drag, keys, − / +).</summary>
    public void UserSet(int ms)
    {
        ms = Clamp(ms);
        if (ms == Value) return;
        Value = ms;
        _slider.Restart();
        if (_config.Scene != Scene.Custom)
        {
            _config.Scene = Scene.Custom;
            _save();
            _scene.Stop(); // the hand-picked value wins over a scene still waiting to apply
        }
        Changed?.Invoke();
    }

    /// <summary>The mouse let go of the slider: the 1 s wait starts again from now.</summary>
    public void Released()
    {
        if (_slider.Enabled) _slider.Restart();
    }

    private void OnSliderTick()
    {
        if (IsHeld()) return; // still held down: wait for the release
        _slider.Stop();
        _apply(Value);
    }

    /// <summary>Switch scene (buttons, tray flyout, hotkey). The slider follows at once; the reconnect waits.</summary>
    public void SelectScene(Scene scene)
    {
        if (scene == _config.Scene)
        {
            Changed?.Invoke();
            return;
        }
        if (_config.Scene == Scene.Custom) // remember the user's own value, even one still inside the slider wait
            _config.CustomLatencyMs = _slider.Enabled ? Value : _config.LatencyMs;
        _config.Scene = scene;
        _save();
        Log.Info($"scene {scene}");

        int ms = Scenes.LatencyMs(scene, Scenes.CustomMs(_config));
        _slider.Stop(); // a pending slider move is superseded
        Value = Clamp(ms);
        _pendingSceneMs = ms;
        _scene.Restart();
        Changed?.Invoke();
    }

    private void OnSceneTick()
    {
        _scene.Stop();
        _apply(_pendingSceneMs);
    }

    /// <summary>Latency quality hint for a value.</summary>
    public static string Hint(int ms) => ms switch
    {
        < 110 => L.T("极限：Wi-Fi 稍有波动就会断续"),
        < 140 => L.T("推荐：打游戏"),
        < 250 => L.T("更稳：Wi-Fi 一般时"),
        _ => L.T("最稳：听歌、看视频"),
    };

    /// <summary>
    /// How much the sound lags the picture: the requested playout delay plus the measured PC-side and
    /// speaker-side extra, plus what the capture path adds (per-app routing). The local API reports the same.
    /// </summary>
    public static int SoundLagMs(int effectiveLatencyMs, AppConfig config, int captureExtraMs) =>
        effectiveLatencyMs + config.VideoDelayExtraMs + captureExtraMs;
}
