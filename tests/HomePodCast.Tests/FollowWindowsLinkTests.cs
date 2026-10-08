using HomePodCast.Audio;

namespace HomePodCast.Tests;

/// <summary>
/// 跟随 Windows as one volume on both sides: alignment only ever lowers, Windows → HomePod and HomePod → Windows follow
/// each other, the cap scales the mapping, and nothing echoes. Everything runs against a fake endpoint (the same
/// echo filter as the real watcher, <see cref="WindowsVolumeFollower"/>) and a fake HomePod — never the Windows volume.
/// </summary>
public class FollowWindowsLinkTests
{
    /// <summary>The HomePod as the link sees it; <see cref="ReportsAppChanges"/> wires it naively (every set reported as an app change).</summary>
    private sealed class FakeHomePod : IFollowHomePod
    {
        public double? Volume { get; set; }
        public bool Muted { get; set; }
        public double Cap { get; set; } = 100;
        public List<double> Sets { get; } = [];
        public List<bool> MuteSets { get; } = [];
        public FollowWindowsLink? ReportsAppChanges { get; set; }

        public void SetVolume(double percent, bool unmute)
        {
            Sets.Add(percent);
            Volume = VolumeLimit.Clamp(percent, Cap);
            if (unmute) Muted = false;
            ReportsAppChanges?.HomePodChanged();
        }

        public void SetMuted(bool muted)
        {
            MuteSets.Add(muted);
            Muted = muted;
        }

        /// <summary>A slider in our app (or a hotkey): the volume changes, then the link is told.</summary>
        public void UserSets(double percent, FollowWindowsLink link)
        {
            Volume = VolumeLimit.Clamp(percent, Cap);
            Muted = false;
            link.HomePodChanged();
        }
    }

    /// <summary>
    /// The watched endpoint: user changes notify with someone else's context; our writes notify with our own
    /// context. Notifications go through a real <see cref="WindowsVolumeFollower"/>, as in VolumeKeyForwarder.
    /// </summary>
    private sealed class FakeEndpoint(EndpointState state)
    {
        private readonly WindowsVolumeFollower _follower = new();
        public EndpointState State { get; private set; } = state;
        public List<float> Writes { get; } = [];
        public FollowWindowsLink? Link { get; set; }

        /// <summary>Stream start / reconnect / mode switched on: the watcher's baseline.</summary>
        public void Baseline()
        {
            _follower.Reset(State);
            Link!.Start(State);
        }

        public void User(float level, bool? muted = null) => Notify(State with { Level = level, Muted = muted ?? State.Muted }, ours: false);

        public void Write(float level)
        {
            Writes.Add(level);
            Notify(State with { Level = level }, ours: true);
        }

        private void Notify(EndpointState now, bool ours)
        {
            State = now;
            if (_follower.Next(now, ours) is { } change) Link!.WindowsChanged(change.From, change.To);
        }
    }

    private static (FakeEndpoint Windows, FakeHomePod HomePod, FollowWindowsLink Link) Setup(float windows, double homePod, double cap = 100,
        bool windowsMuted = false, bool homePodMuted = false)
    {
        var w = new FakeEndpoint(new EndpointState(windowsMuted, windows));
        var hp = new FakeHomePod { Volume = homePod, Cap = cap, Muted = homePodMuted };
        var link = new FollowWindowsLink(hp, w.Write);
        w.Link = link;
        return (w, hp, link);
    }

    // ---------------------------------------------------------------- alignment

    [Fact]
    public void Windows_at_100_and_homepod_at_30_lowers_windows_so_volume_down_steps_from_30()
    {
        var (w, hp, _) = Setup(windows: 1f, homePod: 30);
        w.Baseline();
        Assert.Equal(new[] { 0.30f }, w.Writes);   // Windows down to 30 %
        Assert.Empty(hp.Sets);                      // the HomePod untouched
        w.User(0.28f);                              // volume down
        Assert.Equal(new[] { 28.0 }, hp.Sets);      // 28, not 98
    }

    [Fact]
    public void Windows_below_the_homepod_lowers_the_homepod_and_leaves_windows()
    {
        var (w, hp, _) = Setup(windows: 0.20f, homePod: 50);
        w.Baseline();
        Assert.Equal(new[] { 20.0 }, hp.Sets);
        Assert.Empty(w.Writes);
        w.User(0.22f);
        Assert.Equal(22.0, hp.Volume);
    }

    [Fact]
    public void Already_in_step_nothing_moves()
    {
        var (w, hp, _) = Setup(windows: 0.35f, homePod: 35);
        w.Baseline();
        Assert.Empty(w.Writes);
        Assert.Empty(hp.Sets);
        var (w2, hp2, _) = Setup(windows: 0.70f, homePod: 35, cap: 50);
        w2.Baseline();
        Assert.Empty(w2.Writes);
        Assert.Empty(hp2.Sets);
    }

    [Fact]
    public void Alignment_never_raises_either_side_and_leaves_them_in_step()
    {
        foreach (double cap in new[] { 10.0, 35, 50, 80, 100 })
        for (int wi = 0; wi <= 20; wi++)
        for (int hi = 0; hi <= 20; hi++)
        {
            float windows = wi * 0.05f;
            double homePod = Math.Min(hi * 5.0, cap);
            var (w, hp, _) = Setup(windows, homePod, cap);
            w.Baseline();
            string at = $"W {windows:P0}, HP {homePod}, cap {cap}";
            Assert.True(w.State.Level <= windows + 1e-6f, $"Windows raised: {at}");
            Assert.True(hp.Volume <= homePod + 1e-9, $"HomePod raised: {at}");
            Assert.True(Math.Abs(VolumeKeyRules.FollowPercent(w.State.Level, cap) - hp.Volume!.Value) <= 0.11, $"not in step: {at}");
            Assert.True(w.Writes.Count + hp.Sets.Count <= 1, $"both sides moved: {at}");
        }
    }

    [Fact]
    public void Alignment_lowers_a_muted_homepod_without_unmuting_it()
    {
        var (w, hp, _) = Setup(windows: 0.10f, homePod: 40, homePodMuted: true);
        w.Baseline();
        Assert.Equal(10.0, hp.Volume);
        Assert.True(hp.Muted);
    }

    [Fact]
    public void Mute_is_not_aligned_but_follows_changes()
    {
        var (w, hp, _) = Setup(windows: 0.40f, homePod: 40, windowsMuted: true);
        w.Baseline();
        Assert.False(hp.Muted); // a Windows that was already muted doesn't silence the HomePod at connect
        w.User(0.40f, muted: false);
        w.User(0.40f, muted: true);
        Assert.Equal(new[] { true }, hp.MuteSets);
    }

    [Fact]
    public void Unknown_homepod_volume_aligns_nothing()
    {
        var (w, hp, link) = Setup(windows: 1f, homePod: 0);
        hp.Volume = null;
        w.Baseline();
        Assert.True(link.Active);
        Assert.Empty(w.Writes);
        Assert.Empty(hp.Sets);
    }

    // ---------------------------------------------------------------- following both ways, no echo

    [Fact]
    public void An_app_change_moves_windows_once_and_does_not_come_back()
    {
        var (w, hp, link) = Setup(windows: 0.30f, homePod: 30);
        w.Baseline();
        hp.UserSets(45, link);
        Assert.Equal(new[] { 0.45f }, w.Writes);   // Windows follows, up as well as down
        Assert.Empty(hp.Sets);                      // our own write (our context) is not followed back
        hp.UserSets(45, link);                      // the same value again (the slider's debounce): no second write
        Assert.Single(w.Writes);
        hp.UserSets(12, link);
        Assert.Equal(new[] { 0.45f, 0.12f }, w.Writes);
        Assert.Empty(hp.Sets);
    }

    [Fact]
    public void A_windows_change_moves_the_homepod_once_and_is_not_written_back()
    {
        var (w, hp, _) = Setup(windows: 0.30f, homePod: 30);
        w.Baseline();
        w.User(0.32f);
        w.User(0.32f); // the same state notified again
        Assert.Equal(new[] { 32.0 }, hp.Sets);
        Assert.Empty(w.Writes);
    }

    [Fact]
    public void Even_if_every_homepod_set_were_reported_as_an_app_change_nothing_loops()
    {
        var (w, hp, link) = Setup(windows: 0.30f, homePod: 30, cap: 35);
        w.Baseline();                    // aligns (the HomePod comes down to 10.5)
        int setsBefore = hp.Sets.Count, writesBefore = w.Writes.Count;
        hp.ReportsAppChanges = link;     // the worst wiring: Windows → HomePod → "app change" → Windows → ...
        w.User(0.6f);
        Assert.Equal(1, hp.Sets.Count - setsBefore);            // one HomePod set
        Assert.True(w.Writes.Count - writesBefore <= 1);        // at most one write, with our context: not followed
        w.User(0.62f);
        Assert.Equal(2, hp.Sets.Count - setsBefore);
        Assert.True(w.Writes.Count - writesBefore <= 2);
    }

    [Fact]
    public void Our_writes_are_ignored_by_context_even_if_windows_rounds_them()
    {
        var follower = new WindowsVolumeFollower();
        follower.Reset(new EndpointState(false, 0.30f));
        Assert.Null(follower.Next(new EndpointState(false, 0.4499f), ours: true)); // our 0.45, as the driver stored it
        Assert.NotNull(follower.Next(new EndpointState(false, 0.47f), ours: false));
    }

    // ---------------------------------------------------------------- cap

    [Fact]
    public void The_cap_scales_both_directions()
    {
        var (w, hp, link) = Setup(windows: 0.40f, homePod: 20, cap: 50);
        w.Baseline();
        Assert.Empty(w.Writes);                 // 40 % of a 50 % cap = 20: in step
        w.User(0.42f);
        Assert.Equal(21.0, hp.Volume);
        w.User(1f);
        Assert.Equal(50.0, hp.Volume);          // Windows 100 % = the cap
        hp.UserSets(10, link);
        Assert.Equal(0.20f, w.Writes[^1], 4);   // HomePod 10 of 50 = Windows 20 %
    }

    [Fact]
    public void A_cap_change_aligns_again_lowering_only()
    {
        var (w, hp, link) = Setup(windows: 0.50f, homePod: 50, cap: 100);
        w.Baseline();
        hp.Cap = 50;                            // the controller keeps 50 (at the new cap)
        link.CapChanged();
        Assert.Equal(25.0, hp.Volume);          // Windows 50 % now means 25: the HomePod comes down
        Assert.Empty(w.Writes);
        hp.Cap = 100;
        link.CapChanged();
        Assert.Equal(25.0, hp.Volume);          // not raised back to 50...
        Assert.Equal(new[] { 0.25f }, w.Writes); // ...Windows comes down instead
    }

    [Fact]
    public void Stopped_or_never_started_writes_nothing()
    {
        // e.g. no output device: the route is "intercept", the link never starts, nothing is written
        var (w, hp, link) = Setup(windows: 1f, homePod: 30);
        hp.UserSets(40, link);
        link.CapChanged();
        link.WindowsChanged(new EndpointState(false, 0.5f), new EndpointState(false, 0.6f));
        Assert.Empty(w.Writes);
        Assert.Empty(hp.Sets);

        w.Baseline();
        link.Stop();
        hp.UserSets(20, link);
        w.User(0.8f);
        Assert.Single(w.Writes); // only the alignment at Start
        Assert.Empty(hp.Sets);
    }

    // ---------------------------------------------------------------- the pure rules

    [Theory]
    [InlineData(1f, 30, 100, null, 0.30f)]
    [InlineData(0.98f, 30, 100, null, 0.30f)]
    [InlineData(0.20f, 50, 100, 20.0, null)]
    [InlineData(0.30f, 30, 100, null, null)]
    [InlineData(1f, 30, 50, null, 0.60f)]
    [InlineData(0.40f, 30, 50, 20.0, null)]
    [InlineData(0f, 0, 100, null, null)]
    [InlineData(1f, 0, 100, null, 0f)]
    [InlineData(0f, 40, 100, 0.0, null)]
    public void Align_lowers_the_higher_side(float windows, double homePod, double cap, double? lowerHomePod, float? lowerWindows)
    {
        var a = VolumeKeyRules.Align(windows, homePod, cap);
        Assert.Equal(lowerHomePod, a.HomePod);
        if (lowerWindows is null) Assert.Null(a.WindowsLevel);
        else Assert.Equal(lowerWindows.Value, a.WindowsLevel!.Value, 4);
    }

    [Theory]
    [InlineData(30, 100, 0.30f)]
    [InlineData(25, 50, 0.50f)]
    [InlineData(50, 50, 1f)]
    [InlineData(60, 50, 1f)]   // above the cap (never sent): Windows at most 100 %
    [InlineData(0, 10, 0f)]
    [InlineData(5, 10, 0.5f)]
    public void Windows_level_for_a_homepod_volume_is_the_inverse_mapping(double homePod, double cap, float expected)
    {
        Assert.Equal(expected, VolumeKeyRules.WindowsLevelFor(homePod, cap), 4);
        if (homePod <= cap) Assert.Equal(homePod, VolumeKeyRules.FollowPercent(VolumeKeyRules.WindowsLevelFor(homePod, cap), cap), 1);
    }
}
