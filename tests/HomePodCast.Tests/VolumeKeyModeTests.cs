using HomePodCast.Audio;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>VolumeKeyMode: config migration, the route per mode, the hook's swallow rule, key steps, and FollowWindows.</summary>
public class VolumeKeyModeTests
{
    private const int VkUp = VolumeKeyRules.VkVolumeUp, VkDown = VolumeKeyRules.VkVolumeDown, VkMute = VolumeKeyRules.VkVolumeMute;

    /// <summary>Volume keys, media keys next to them (prev/next/stop/play), letters, Space, Enter, Ctrl, Alt, F1, Win.</summary>
    private static readonly int[] OtherKeys = [0xB0, 0xB1, 0xB2, 0xB3, 0x41, 0x5A, 0x20, 0x0D, 0x11, 0x12, 0x70, 0x5B, 0xAC, 0xB4];

    private static readonly int[] VolumeKeys = [VkUp, VkDown, VkMute];

    // ---------------------------------------------------------------- migration

    [Theory]
    [InlineData(true, VolumeKeyMode.FollowWindows)]   // the old default: keys reach the HomePod by following Windows
    [InlineData(false, VolumeKeyMode.Off)]            // switched off by the user: stays off
    public void The_old_switch_becomes_a_mode(bool forward, VolumeKeyMode expected)
    {
        foreach (var current in Enum.GetValues<VolumeKeyMode>())
            Assert.Equal(expected, VolumeKeyRules.Migrate(forward, current));
    }

    [Fact]
    public void Without_the_old_switch_the_mode_is_kept_and_unknown_values_fall_back_to_the_default()
    {
        foreach (var mode in Enum.GetValues<VolumeKeyMode>()) Assert.Equal(mode, VolumeKeyRules.Migrate(null, mode));
        Assert.Equal(VolumeKeyMode.FollowWindows, VolumeKeyRules.Migrate(null, (VolumeKeyMode)42));
        Assert.Equal(VolumeKeyMode.FollowWindows, new AppConfig().VolumeKeys);
    }

    [Theory]
    [InlineData("""{ "ForwardVolumeKeys": true }""", VolumeKeyMode.FollowWindows)]
    [InlineData("""{ "ForwardVolumeKeys": false }""", VolumeKeyMode.Off)]
    [InlineData("""{ "LatencyMs": 140 }""", VolumeKeyMode.FollowWindows)]
    [InlineData("""{ "VolumeKeys": "WhileStreaming" }""", VolumeKeyMode.WhileStreaming)]
    [InlineData("""{ "VolumeKeys": "WhenWindowsMuted" }""", VolumeKeyMode.WhenWindowsMuted)]
    [InlineData("""{ "VolumeKeys": "Off" }""", VolumeKeyMode.Off)]
    [InlineData("""{ "VolumeKeys": 7 }""", VolumeKeyMode.FollowWindows)]
    public void Loading_a_config_migrates_and_saving_writes_only_the_mode(string json, VolumeKeyMode expected)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, json);
            var cfg = AppConfig.Load(path);
            Assert.Equal(expected, cfg.VolumeKeys);
            Assert.Null(cfg.ForwardVolumeKeys);

            cfg.Save(path);
            var saved = File.ReadAllText(path);
            Assert.Contains($"\"VolumeKeys\": \"{expected}\"", saved);   // by name, like Scene
            Assert.DoesNotContain("ForwardVolumeKeys", saved);
            Assert.Equal(expected, AppConfig.Load(path).VolumeKeys);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_fresh_install_follows_windows()
    {
        var cfg = AppConfig.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "config.json"));
        Assert.Equal(VolumeKeyMode.FollowWindows, cfg.VolumeKeys);
    }

    // ---------------------------------------------------------------- route: follow / intercept / pass-through

    [Theory]
    [InlineData(VolumeKeyMode.WhileStreaming, true, true, VolumeKeyRoute.Intercept)]
    [InlineData(VolumeKeyMode.WhileStreaming, true, false, VolumeKeyRoute.Intercept)]
    [InlineData(VolumeKeyMode.WhileStreaming, false, true, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.WhileStreaming, false, false, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.FollowWindows, true, true, VolumeKeyRoute.Follow)]
    [InlineData(VolumeKeyMode.FollowWindows, true, false, VolumeKeyRoute.Intercept)]    // no output device: take the keys
    [InlineData(VolumeKeyMode.FollowWindows, false, true, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.FollowWindows, false, false, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.WhenWindowsMuted, true, true, VolumeKeyRoute.Forward)]
    [InlineData(VolumeKeyMode.WhenWindowsMuted, true, false, VolumeKeyRoute.Intercept)]
    [InlineData(VolumeKeyMode.WhenWindowsMuted, false, true, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.Off, true, true, VolumeKeyRoute.PassThrough)]
    [InlineData(VolumeKeyMode.Off, true, false, VolumeKeyRoute.PassThrough)]           // off means off, device or not
    [InlineData(VolumeKeyMode.Off, false, false, VolumeKeyRoute.PassThrough)]
    public void Mode_stream_and_output_device_decide_the_route(VolumeKeyMode mode, bool streaming, bool device, VolumeKeyRoute expected)
    {
        Assert.Equal(expected, VolumeKeyRules.RouteFor(mode, streaming, device));
        Assert.Equal(expected == VolumeKeyRoute.Intercept, VolumeKeyRules.HookWanted(mode, streaming, device));
        Assert.Equal(expected switch
        {
            VolumeKeyRoute.Follow => EndpointMode.Follow,
            VolumeKeyRoute.Forward => EndpointMode.Forward,
            _ => EndpointMode.Off,
        }, VolumeKeyRules.EndpointModeFor(expected));
    }

    // ---------------------------------------------------------------- the hook's decision

    [Fact]
    public void Only_volume_keys_are_swallowed_and_only_while_streaming_in_WhileStreaming_mode()
    {
        foreach (var mode in Enum.GetValues<VolumeKeyMode>())
        foreach (var streaming in new[] { false, true })
        foreach (var vk in VolumeKeys.Concat(OtherKeys))
        {
            var d = VolumeKeyRules.Decide(mode, streaming, outputDevice: true, vk, keyDown: true, downSwallowed: false);
            bool expected = mode == VolumeKeyMode.WhileStreaming && streaming && VolumeKeys.Contains(vk);
            Assert.True(expected == d.Swallow, $"{mode}, streaming {streaming}, vk 0x{vk:X2}: swallow {d.Swallow}");
            Assert.Equal(expected ? VolumeKeyRules.CommandFor(vk) : VolumeKeyCommand.None, d.Command);
        }
    }

    [Fact]
    public void Without_an_output_device_the_keys_are_taken_in_the_following_modes_too_but_never_other_keys()
    {
        foreach (var mode in Enum.GetValues<VolumeKeyMode>())
        foreach (var streaming in new[] { false, true })
        foreach (var vk in VolumeKeys.Concat(OtherKeys))
        {
            var d = VolumeKeyRules.Decide(mode, streaming, outputDevice: false, vk, keyDown: true, downSwallowed: false);
            bool expected = mode != VolumeKeyMode.Off && streaming && VolumeKeys.Contains(vk);
            Assert.True(expected == d.Swallow, $"{mode}, streaming {streaming}, vk 0x{vk:X2}, no device: swallow {d.Swallow}");
        }
    }

    [Fact]
    public void Other_keys_are_never_swallowed_even_with_stale_state()
    {
        foreach (var vk in OtherKeys)
        foreach (var down in new[] { false, true })
            Assert.Equal(default, VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, false, vk, down, downSwallowed: true));
    }

    [Fact]
    public void A_key_up_is_swallowed_exactly_when_its_key_down_was()
    {
        foreach (var vk in VolumeKeys)
        {
            Assert.Equal(new HookDecision(true, VolumeKeyCommand.None),
                VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, vk, keyDown: false, downSwallowed: true));
            // pressed before the hook took over: its key-up goes on to Windows too
            Assert.Equal(default, VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, vk, keyDown: false, downSwallowed: false));
            // stopped streaming while held: the key-up still matches the swallowed key-down
            Assert.True(VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, false, true, vk, keyDown: false, downSwallowed: true).Swallow);
        }
    }

    [Fact]
    public void Auto_repeat_steps_again_but_mute_toggles_once_per_press()
    {
        // A held key: the first key-down, then repeats (the hook remembers the swallowed key-down).
        foreach (var (vk, command) in new[] { (VkUp, VolumeKeyCommand.Up), (VkDown, VolumeKeyCommand.Down) })
        {
            Assert.Equal(new HookDecision(true, command), VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, vk, true, false));
            for (int i = 0; i < 5; i++)
                Assert.Equal(new HookDecision(true, command), VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, vk, true, true));
        }
        Assert.Equal(new HookDecision(true, VolumeKeyCommand.Mute), VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, VkMute, true, false));
        Assert.Equal(new HookDecision(true, VolumeKeyCommand.None), VolumeKeyRules.Decide(VolumeKeyMode.WhileStreaming, true, true, VkMute, true, true));
    }

    // ---------------------------------------------------------------- key → step

    [Fact]
    public void Volume_keys_map_to_commands_and_two_percent_steps()
    {
        Assert.Equal(VolumeKeyCommand.Up, VolumeKeyRules.CommandFor(0xAF));
        Assert.Equal(VolumeKeyCommand.Down, VolumeKeyRules.CommandFor(0xAE));
        Assert.Equal(VolumeKeyCommand.Mute, VolumeKeyRules.CommandFor(0xAD));
        foreach (var vk in OtherKeys) Assert.Equal(VolumeKeyCommand.None, VolumeKeyRules.CommandFor(vk));
        Assert.Equal(2, VolumeKeyRules.StepFor(VolumeKeyCommand.Up));
        Assert.Equal(-2, VolumeKeyRules.StepFor(VolumeKeyCommand.Down));
        Assert.Equal(0, VolumeKeyRules.StepFor(VolumeKeyCommand.Mute));
        Assert.Equal(0, VolumeKeyRules.StepFor(VolumeKeyCommand.None));
    }

    [Theory]
    [InlineData(50, VolumeKeyCommand.Up, 100, 52)]
    [InlineData(50, VolumeKeyCommand.Down, 100, 48)]
    [InlineData(99, VolumeKeyCommand.Up, 100, 100)]
    [InlineData(1, VolumeKeyCommand.Down, 100, 0)]
    [InlineData(0, VolumeKeyCommand.Down, 100, 0)]
    [InlineData(59, VolumeKeyCommand.Up, 60, 60)]   // the cap holds
    [InlineData(60, VolumeKeyCommand.Up, 60, 60)]
    [InlineData(70, VolumeKeyCommand.Down, 60, 60)] // above a newly lowered cap: back under it
    [InlineData(40, VolumeKeyCommand.Mute, 100, 40)]
    public void A_step_stays_between_zero_and_the_cap(double current, VolumeKeyCommand command, double cap, double expected) =>
        Assert.Equal(expected, VolumeKeyRules.Next(current, command, cap), 6);

    [Fact]
    public void Holding_volume_up_climbs_two_percent_per_repeat_up_to_the_cap()
    {
        double v = 30;
        var seen = new List<double>();
        for (int i = 0; i < 20; i++) seen.Add(v = VolumeKeyRules.Next(v, VolumeKeyCommand.Up, 50));
        Assert.Equal(new double[] { 32, 34, 36, 38, 40, 42, 44, 46, 48, 50, 50, 50 }, seen.Take(12));
    }

    // ---------------------------------------------------------------- FollowWindows

    [Theory]
    [InlineData(0.5f, 100, 50)]
    [InlineData(0.5f, 40, 20)]
    [InlineData(1f, 40, 40)]     // Windows at 100 % = the cap
    [InlineData(0f, 40, 0)]
    [InlineData(0.02f, 50, 1)]   // one Windows key step = cap / 50
    [InlineData(1.3f, 100, 100)] // out of range from a driver: clamped
    [InlineData(-0.1f, 100, 0)]
    public void Windows_level_scales_to_the_cap(float level, double cap, double expected) =>
        Assert.Equal(expected, VolumeKeyRules.FollowPercent(level, cap), 6);

    [Fact]
    public void The_whole_windows_range_spans_zero_to_the_cap()
    {
        const double cap = 60;
        var percents = Enumerable.Range(0, 51).Select(i => VolumeKeyRules.FollowPercent(i * 0.02f, cap)).ToList();
        Assert.Equal(0, percents[0]);
        Assert.Equal(cap, percents[^1], 6);
        Assert.True(percents.Zip(percents.Skip(1)).All(p => p.Second > p.First), "strictly rising");
    }

    [Fact]
    public void Mute_follows_mute_and_a_new_level_follows_the_level()
    {
        var at40 = new EndpointState(false, 0.40f);
        Assert.Equal(new FollowAction(42, null), VolumeKeyRules.Follow(at40, new EndpointState(false, 0.42f), 100));
        Assert.Equal(new FollowAction(21, null), VolumeKeyRules.Follow(at40, new EndpointState(false, 0.42f), 50));
        Assert.Equal(new FollowAction(null, true), VolumeKeyRules.Follow(at40, new EndpointState(true, 0.40f), 100));
        Assert.Equal(new FollowAction(null, false), VolumeKeyRules.Follow(new EndpointState(true, 0.40f), at40, 100));
        // volume up on a muted Windows: unmutes and steps
        Assert.Equal(new FollowAction(42, false), VolumeKeyRules.Follow(new EndpointState(true, 0.40f), new EndpointState(false, 0.42f), 100));
        // the level moved while Windows stays muted: the HomePod stays muted (no unmuting by a volume)
        Assert.True(VolumeKeyRules.Follow(new EndpointState(true, 0.40f), new EndpointState(true, 0.60f), 100).IsNone);
        // nothing changed (a channel-only change): nothing to do
        Assert.True(VolumeKeyRules.Follow(at40, at40, 100).IsNone);
    }

    [Fact]
    public void Follower_takes_a_baseline_first_and_never_follows_our_own_changes()
    {
        var f = new WindowsVolumeFollower();
        var a = new EndpointState(false, 0.30f);
        var b = new EndpointState(false, 0.32f);
        var c = new EndpointState(false, 0.50f);

        Assert.Null(f.Next(a, ours: false));      // no baseline yet (stream just started): the HomePod is not moved
        Assert.Equal((a, b), f.Next(b, ours: false));
        Assert.Null(f.Next(b, ours: false));      // the same state again (another notification for it)
        Assert.Null(f.Next(c, ours: true));       // written with our own event context: not followed...
        Assert.Null(f.Next(c, ours: false));      // ...and its echo isn't either
        Assert.Equal((c, a), f.Next(a, ours: false)); // the next real change starts from where we left Windows

        f.Reset(b);                               // stream start / mode or device change: baseline only
        Assert.Null(f.Next(b, ours: false));
        Assert.Equal((b, c), f.Next(c, ours: false));
        f.Reset();
        Assert.Null(f.Next(a, ours: false));
    }

    [Fact]
    public void Following_a_change_and_hearing_it_again_moves_the_homepod_once()
    {
        // The loop that must not happen: Windows change → HomePod set → (notification for the same state) → set again.
        var f = new WindowsVolumeFollower();
        f.Reset(new EndpointState(false, 0.30f));
        int sets = 0;
        foreach (var level in new[] { 0.32f, 0.32f, 0.32f, 0.34f, 0.34f })
            if (f.Next(new EndpointState(false, level), ours: false) is { } change && !VolumeKeyRules.Follow(change.From, change.To, 100).IsNone)
                sets++;
        Assert.Equal(2, sets);
    }

    // ---------------------------------------------------------------- OSD

    [Theory]
    [InlineData(1, true)]   // QUNS_NOT_PRESENT
    [InlineData(2, true)]   // QUNS_BUSY: borderless full screen (video, windowed game) — shown without taking the focus
    [InlineData(3, false)]  // QUNS_RUNNING_D3D_FULL_SCREEN: exclusive full-screen game
    [InlineData(4, false)]  // QUNS_PRESENTATION_MODE
    [InlineData(5, true)]   // QUNS_ACCEPTS_NOTIFICATIONS
    [InlineData(6, true)]   // QUNS_QUIET_TIME
    [InlineData(7, true)]   // QUNS_APP
    public void Osd_is_skipped_over_exclusive_full_screen_and_presentations(int state, bool shown) =>
        Assert.Equal(shown, VolumeOsd.AllowedFor(state));
}
