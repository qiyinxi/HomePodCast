using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>The scene buttons and the latency slider on 首页: when the reconnect happens and with which value.</summary>
public class LatencyTunerTests
{
    private sealed class FakeDebounce : IDebounce
    {
        public bool Enabled { get; private set; }
        public int Restarts { get; private set; }
        public event Action? Tick;

        public void Restart()
        {
            Enabled = true;
            Restarts++;
        }

        public void Stop() => Enabled = false;

        /// <summary>The timer elapses (only does something while running).</summary>
        public void Elapse()
        {
            if (Enabled) Tick?.Invoke();
        }
    }

    private readonly FakeDebounce _slider = new(), _scene = new();
    private readonly List<int> _applied = [];
    private int _saves;

    private LatencyTuner Tuner(AppConfig cfg, int floor = 100) =>
        new(cfg, _applied.Add, _slider, _scene, floor, save: () => _saves++);

    [Fact]
    public void Slider_reconnects_only_after_release_and_a_quiet_second()
    {
        var cfg = new AppConfig { Scene = Scene.Custom, LatencyMs = 120 };
        bool held = true;
        var t = Tuner(cfg);
        t.IsHeld = () => held;

        t.UserSet(130);
        t.UserSet(131);
        Assert.True(t.Pending);
        _slider.Elapse(); // a second passed but the mouse still holds the thumb
        Assert.Empty(_applied);
        Assert.True(_slider.Enabled);

        held = false;
        int before = _slider.Restarts;
        t.Released(); // the wait starts again from the release
        Assert.Equal(before + 1, _slider.Restarts);
        _slider.Elapse();
        Assert.Equal([131], _applied);
        Assert.False(t.Pending);
    }

    [Fact]
    public void Keyboard_steps_restart_the_wait_each_time()
    {
        var t = Tuner(new AppConfig { Scene = Scene.Custom, LatencyMs = 120 });
        t.UserSet(121);
        t.UserSet(122);
        t.UserSet(132);
        Assert.Equal(3, _slider.Restarts);
        _slider.Elapse();
        Assert.Equal([132], _applied);
    }

    [Fact]
    public void Values_below_the_floor_cannot_be_chosen()
    {
        var t = Tuner(new AppConfig { Scene = Scene.Custom, LatencyMs = 90 }, floor: 110);
        Assert.Equal(110, t.Value); // a saved value below the floor is shown at the floor
        t.UserSet(50);
        Assert.Equal(110, t.Value);
        Assert.Empty(_applied);
        t.UserSet(LatencyTuner.MaxMs + 100);
        Assert.Equal(LatencyTuner.MaxMs, t.Value);
    }

    [Fact]
    public void Moving_the_slider_in_a_preset_switches_to_custom_and_cancels_the_scene_reconnect()
    {
        var cfg = new AppConfig { Scene = Scene.Custom, LatencyMs = 120 };
        var t = Tuner(cfg);
        t.SelectScene(Scene.Music);
        Assert.Equal(Scene.Music, cfg.Scene);
        Assert.Equal(300, t.Value);
        Assert.True(_scene.Enabled);

        t.UserSet(280);
        Assert.Equal(Scene.Custom, cfg.Scene);
        Assert.False(_scene.Enabled); // the music scene never reconnects
        _scene.Elapse();
        _slider.Elapse();
        Assert.Equal([280], _applied);
    }

    [Fact]
    public void Cycling_through_scenes_reconnects_once_with_the_last_one()
    {
        var cfg = new AppConfig { Scene = Scene.Custom, LatencyMs = 140 };
        var t = Tuner(cfg);
        t.SelectScene(Scene.Game);
        t.SelectScene(Scene.Music);
        t.SelectScene(Scene.Movie);
        Assert.Empty(_applied);
        _scene.Elapse();
        Assert.Equal([Scenes.MovieMs], _applied);
        Assert.Equal(140, cfg.CustomLatencyMs); // 自定义 keeps the user's own value

        t.SelectScene(Scene.Custom);
        Assert.Equal(140, t.Value);
        _scene.Elapse();
        Assert.Equal([Scenes.MovieMs, 140], _applied);
    }

    [Fact]
    public void A_value_still_waiting_on_the_slider_is_remembered_as_the_custom_one()
    {
        var cfg = new AppConfig { Scene = Scene.Custom, LatencyMs = 120 };
        var t = Tuner(cfg);
        t.UserSet(133); // not applied yet
        t.SelectScene(Scene.Game);
        Assert.Equal(133, cfg.CustomLatencyMs);
        Assert.False(_slider.Enabled); // the slider's pending reconnect is superseded
        _scene.Elapse();
        Assert.Equal([Scenes.GameMs], _applied);
    }

    [Fact]
    public void Picking_the_current_scene_changes_nothing()
    {
        var cfg = new AppConfig { Scene = Scene.Game, LatencyMs = Scenes.GameMs };
        var t = Tuner(cfg);
        int changed = 0;
        t.Changed += () => changed++;
        t.SelectScene(Scene.Game);
        Assert.Equal(1, changed); // the UI refreshes
        Assert.False(t.Pending);
        Assert.Equal(0, _saves);
    }

    [Fact]
    public void A_scene_below_the_floor_shows_the_floor_but_asks_for_its_own_latency()
    {
        var cfg = new AppConfig { Scene = Scene.Custom, LatencyMs = 150 };
        var t = Tuner(cfg, floor: 110);
        t.SelectScene(Scene.Game);
        Assert.Equal(110, t.Value);
        _scene.Elapse();
        Assert.Equal([Scenes.GameMs], _applied); // the controller applies the same floor when it connects
    }

    [Fact]
    public void Raising_the_floor_moves_the_shown_value_without_reconnecting()
    {
        var t = Tuner(new AppConfig { Scene = Scene.Custom, LatencyMs = 105 }, floor: 100);
        int changed = 0;
        t.Changed += () => changed++;
        t.Floor = 112;
        Assert.Equal(112, t.Value);
        Assert.Equal(1, changed);
        Assert.False(t.Pending);
        Assert.Empty(_applied);
    }

    [Fact]
    public void Sound_lag_is_latency_plus_measured_extra_plus_capture_path()
    {
        var cfg = new AppConfig { VideoDelayExtraMs = 36 };
        Assert.Equal(156, LatencyTuner.SoundLagMs(120, cfg, captureExtraMs: 0));
        Assert.Equal(191, LatencyTuner.SoundLagMs(120, cfg, Audio.RoutedCapture.RoutedExtraLatencyMs));
    }
}
