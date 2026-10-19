using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

/// <summary>
/// The wireless cinema window, built offscreen (never shown) against a fake app: its volume, its errors and what it
/// does when another connection takes over. A generated WAV is decoded; nothing plays, no microphone opens.
/// </summary>
public class CinemaFormTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_saved_surround_position_is_preserved_and_blocked_until_its_receiver_is_independent_again(bool joinsPair) => OnSta(() =>
    {
        string surround = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        string stereo = CinemaTests.TempWave(16, 2, CinemaTests.Stereo, 48000);
        try
        {
            var host = new FakeHost();
            host.Config.GroupSpeakers!.Add(new() { DeviceId = "AA:00:00:00:00:04", Name = "后环绕", Position = SpeakerPosition.RearLeft });
            var rear = host.Found.Single(d => d.DeviceId == "AA:00:00:00:00:04");
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            FluentComboBox Rear() => All<FluentComboBox>(page).Single(c => c.AccessibleName == "后环绕 位置");
            Pump(page, page.UseFile(surround));
            Assert.True(Rear().Enabled); Assert.Equal("后左", Rear().SelectedText);
            host.Found.Remove(rear);
            if (joinsPair) host.Found.Add(rear with { Txt = new Dictionary<string, string> { ["tsid"] = "saved-speaker-pair" } });
            page.RefreshSpeakers(); page.RefreshStatus();
            Assert.Equal("后左", Rear().SelectedText); Assert.False(Rear().Enabled);
            Assert.False(All<FluentButton>(page).Single(b => b.Text == "播放").Enabled);
            Assert.False(All<FluentButton>(page).Single(b => b.Text == "应用").Enabled);
            Assert.Contains(All<TextBlock>(page), t => t.Text.Contains(joinsPair ? "当前不是独立的 HomePod" : "无法确认它是否支持已保存的离散位置"));
            Pump(page, page.PlayAsync());
            Assert.Empty(host.Started);
            Assert.Equal(SpeakerPosition.RearLeft, host.Config.GroupSpeakers![2].Position);
            Assert.Empty(host.Config.Cinema.Speakers);
            Pump(page, page.UseFile(stereo));
            Assert.Equal("后左", Rear().SelectedText); Assert.False(Rear().Enabled);
            Assert.True(All<FluentButton>(page).Single(b => b.Text == "播放").Enabled); // the retained stereo-file role stays silent
            host.Found.RemoveAll(d => d.DeviceId == rear.DeviceId); host.Found.Add(rear);
            page.RefreshSpeakers();
            Pump(page, page.UseFile(surround));
            Assert.True(Rear().Enabled); Assert.Equal("后左", Rear().SelectedText);
            Pump(page, page.PlayAsync());
            Assert.Equal(CinemaRole.RearLeft, Assert.Single(host.Started).Speakers[2].Role);
            Assert.Equal(CinemaRole.RearLeft, host.Config.Cinema.Speakers[2].Role);
        }
        finally { File.Delete(surround); File.Delete(stereo); }
    });

    [Fact]
    public void Local_file_groups_explain_and_block_the_unsupported_apple_tv_pairing_path() => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            host.Config.GroupSpeakers!.Add(new() { DeviceId = "AA:00:00:00:00:03", Name = "Apple TV", Position = SpeakerPosition.Stereo });
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            Pump(page, page.UseFile(wav));
            Assert.False(All<FluentButton>(page).Single(b => b.Text == "播放").Enabled);
            Assert.False(All<FluentButton>(page).Single(b => b.Text == "应用").Enabled);
            Assert.Contains(All<TextBlock>(page), t => t.Text == "本地文件分组播放暂不支持 Apple TV 的配对连接；请使用 HomePod。");
            Pump(page, page.PlayAsync());
            Assert.Empty(host.Started);
        }
        finally { File.Delete(wav); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opening_a_file_freezes_layout_and_apply_until_success_or_failure(bool openedSuccessfully) => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            Pump(page, page.UseFile(wav));
            var ready = new TaskCompletionSource<bool>();
            page.WaitForFileOpened = _ => ready.Task;
            var start = page.PlayAsync();
            Assert.StartsWith("the decoder is ", page.OpeningState, StringComparison.Ordinal); // what a timeout would report
            page.RefreshStatus(); // the periodic refresh must not re-enable editing while the decoder opens
            var apply = All<FluentButton>(page).Single(b => b.Text == "应用");
            var remove = All<FluentButton>(page).Single(b => b.AccessibleName == "移除 书房");
            Assert.False(apply.Enabled); Assert.False(remove.Enabled);
            Assert.False(All<FluentButton>(page).Single(b => b.Text == "播放").Enabled);
            Assert.All(All<FluentComboBox>(page).Where(c => c.AccessibleName?.EndsWith(" 位置") == true), c => Assert.False(c.Enabled));
            Assert.All(All<FluentSlider>(page).Where(c => c.AccessibleName != "音量"), c => Assert.False(c.Enabled));
            remove.PerformClick(); apply.PerformClick();
            ready.SetResult(openedSuccessfully);
            Pump(page, start);
            Assert.Equal("nothing is opening", page.OpeningState);
            if (openedSuccessfully)
            {
                var (_, speakers, _) = Assert.Single(host.Started);
                Assert.Equal(2, speakers.Length);
                Assert.Equal(2, host.Config.GroupSpeakers!.Count);
                page.PageHidden();
            }
            else Assert.Empty(host.Started);
            Assert.True(apply.Enabled);
            Assert.True(All<FluentComboBox>(page).Single(c => c.AccessibleName == "书房 位置").Enabled);
        }
        finally { File.Delete(wav); }
    });

    [Fact]
    public void A_stereo_file_keeps_saved_surround_positions_silent_and_restores_them_for_51() => OnSta(() =>
    {
        string surround = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        string stereo = CinemaTests.TempWave(16, 2, CinemaTests.Stereo, 48000);
        try
        {
            var host = new FakeHost();
            host.Config.GroupSpeakers!.Add(new() { DeviceId = "AA:00:00:00:00:04", Name = "后环绕", Position = SpeakerPosition.RearLeft });
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            FluentComboBox Rear() => All<FluentComboBox>(page).Single(c => c.AccessibleName == "后环绕 位置");
            Pump(page, page.UseFile(surround));
            Assert.Equal("后左", Rear().SelectedText); Assert.True(Rear().Enabled);
            Pump(page, page.UseFile(stereo));
            Assert.Equal("后左", Rear().SelectedText); Assert.False(Rear().Enabled);
            Assert.Contains(All<TextBlock>(page), t => t.Text.Contains("此音箱保持静音"));
            Pump(page, page.PlayAsync());
            var (source, layout, _) = Assert.Single(host.Started);
            Assert.Equal(CinemaRole.RearLeft, layout[2].Role);
            Assert.True(SpinWait.SpinUntil(() => source.BufferedFrames >= RtpSender.FramesPerPacket, TimeSpan.FromSeconds(2)),
                $"the decoder did not fill the ring: {source.BufferedFrames} frames buffered, {source.Progress}");
            Assert.True(source.ReadPacket(0));
            var pcm = new float[RtpSender.FramesPerPacket * 2];
            source.ReadTarget(0, pcm);
            Assert.Contains(pcm, sample => Math.Abs(sample) > 0.001f); // prove the packet contains the generated file, not startup silence
            source.ReadTarget(2, pcm);
            Assert.All(pcm, sample => Assert.Equal(0, sample));
            Assert.Equal(SpeakerPosition.RearLeft, host.Config.GroupSpeakers![2].Position);
            Pump(page, page.UseFile(surround));
            Assert.Equal("后左", Rear().SelectedText); Assert.True(Rear().Enabled);
        }
        finally { File.Delete(surround); File.Delete(stereo); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_file_opening_in_the_background_cannot_start_after_leaving_its_source(bool hidePage) => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            Pump(page, page.UseFile(wav));
            var opened = new TaskCompletionSource<bool>();
            page.WaitForFileOpened = _ => opened.Task;
            var start = page.PlayAsync();
            Assert.False(start.IsCompleted);
            if (hidePage) page.PageHidden();
            else All<FluentComboBox>(page).Single(c => c.AccessibleName == "音源").SelectedIndex = 0;
            opened.SetResult(true);
            Pump(page, start);
            Assert.Empty(host.Started);
        }
        finally { File.Delete(wav); }
    });

    [Fact]
    public void Playing_and_stopping_a_file_never_activates_a_saved_live_group() => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            host.Config.MultiRoomDeviceId = null;
            using var form = Form(host);
            Assert.Null(GroupPlan.FromConfig(host.Config));
            Pump(form, form.UseFile(wav));
            Pump(form, form.PlayAsync());
            Assert.Single(host.Started);
            Assert.Null(host.Config.GroupCaptureDeviceIds);
            Assert.Null(GroupPlan.FromConfig(host.Config));
            host.State = CinemaState.Ended;
            form.RefreshStatus();
            Assert.Null(GroupPlan.FromConfig(host.Config));
            var restored = new AppConfig();
            UsageProfiles.Apply(restored, UsageProfiles.Capture(host.Config));
            Assert.Null(GroupPlan.FromConfig(restored));
        }
        finally { File.Delete(wav); }
    });

    private sealed class FakeHost : ICinemaHost
    {
        public AppConfig Config { get; } = new() { Volume = 60, DeviceId = "AA:00:00:00:00:01", DeviceName = "客厅",
            MultiRoomDeviceId = "AA:00:00:00:00:02", MultiRoomDeviceName = "书房", GroupSpeakers = [
                new() { DeviceId = "AA:00:00:00:00:01", Name = "客厅", Position = SpeakerPosition.Left },
                new() { DeviceId = "AA:00:00:00:00:02", Name = "书房", Position = SpeakerPosition.Right } ] };
        public IReadOnlyList<AirPlayDevice> Speakers => Found;
        public List<AirPlayDevice> Found { get; } =
        [
            new("客厅", "AA:00:00:00:00:01", IPAddress.Parse("192.168.50.11"), 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
            new("书房", "AA:00:00:00:00:02", IPAddress.Parse("192.168.50.12"), 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
            new("Apple TV", "AA:00:00:00:00:03", IPAddress.Parse("192.168.50.13"), 7000, "AppleTV14,1", new Dictionary<string, string>()),
            new("后环绕", "AA:00:00:00:00:04", IPAddress.Parse("192.168.50.14"), 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
        ];
        public double? Volume => Config.Volume;
        public double VolumeCap => Config.VolumeCapPercent;
        public string StatusText => "正在连接…";
        public int LatencyMs => 200;
        public CinemaState State { get; set; } = CinemaState.Waiting;
        public List<(CinemaFileSource Source, CinemaSpeaker[] Speakers, double Volume)> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<double> Previewed { get; } = [];
        public List<double> Applied { get; } = [];

        public CinemaState CinemaStateOf(CinemaFileSource source) => State;
        public void StartCinema(CinemaFileSource source, IReadOnlyList<CinemaSpeaker> speakers, double volume) =>
            Started.Add((source, speakers.ToArray(), volume));
        public void StopCinema(CinemaFileSource source, string reason)
        {
            Stopped.Add(reason);
            source.Dispose();
        }
        public void PreviewVolume(double percent) => Previewed.Add(percent);
        public void ApplyVolume(double percent)
        {
            Applied.Add(percent);
            Config.Volume = percent;
        }
        public IDisposable LimitVolumeForMeasurement() => throw new InvalidOperationException("no measurement in these tests");
    }

    /// <summary>The slider's save debounce, fired by hand.</summary>
    private sealed class ManualDebounce : IDebounce
    {
        public bool Enabled { get; private set; }
        public event Action? Tick;
        public void Restart() => Enabled = true;
        public void Stop() => Enabled = false;
        public void Fire() => Tick?.Invoke();
    }

    private static CinemaForm Form(FakeHost host, ManualDebounce? wait = null) => new(host, inputs: [], volumeWait: wait ?? new ManualDebounce());

    /// <summary>The shared per-device position controls, never a second cinema layout.</summary>
    private static void ChooseFronts(Form form, bool right = true)
    {
        var combos = All<FluentComboBox>(form).Where(c => c.AccessibleName?.EndsWith(" 位置") == true).ToList();
        combos.Single(c => c.AccessibleName == "客厅 位置").SelectedIndex = 1;
        combos.Single(c => c.AccessibleName == "书房 位置").SelectedIndex = right ? 2 : 1;
    }

    /// <summary>The window's message loop until <paramref name="task"/> completes; a timeout says what the window waited for.</summary>
    private static void Pump(Control window, Task task, [CallerArgumentExpression(nameof(task))] string what = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        // 2026-10-16 (CI run 38970600060): "Play did not finish", for a UseFile whose file inspection took more than 20 s.
        string opening = window switch { CinemaPage page => page.OpeningState, CinemaForm form => form.OpeningState, _ => "" };
        Assert.True(task.IsCompleted, $"{what} did not finish within 20 s: {opening}; status \"{Status(window)}\"");
        task.GetAwaiter().GetResult();
    }

    private static string Status(Control window) => All<TextBlock>(window).Select(t => t.Text).FirstOrDefault(t =>
        t.StartsWith("影院播放失败", StringComparison.Ordinal) || t.StartsWith("无线影院已停止", StringComparison.Ordinal)
        || t.StartsWith("正在", StringComparison.Ordinal) || t.StartsWith("已暂停", StringComparison.Ordinal)) ?? "";

    [Fact]
    public void A_persistent_page_follows_later_cap_changes_and_flushes_its_pending_volume() => OnSta(() =>
    {
        var host = new FakeHost();
        var wait = new ManualDebounce();
        using var page = new CinemaPage(host, inputs: [], volumeWait: wait);
        var volume = All<FluentSlider>(page).Single(slider => slider.AccessibleName == "音量");
        Assert.Equal(100, volume.Ceiling);
        host.Config.VolumeCapPercent = 25;
        page.PageShown();
        page.PageHidden();
        Assert.Equal(25, volume.Ceiling);
        Assert.Equal(25, volume.Value);
        volume.UserSetValue(20);
        page.Flush();
        Assert.Equal([20.0], host.Applied);
        Assert.False(wait.Enabled);
    });

    [Fact]
    public void The_windows_volume_is_what_the_cinema_starts_with_and_it_is_saved() => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            var wait = new ManualDebounce();
            using var form = Form(host, wait);
            var volume = All<FluentSlider>(form).Single(s => s.AccessibleName == "音量");
            Assert.Equal(60, volume.Value); // the app's volume

            ChooseFronts(form);
            Pump(form, form.UseFile(wav));
            volume.UserSetValue(10);
            Assert.Equal([10.0], host.Previewed); // the speakers follow the drag
            Assert.True(wait.Enabled);

            Pump(form, form.PlayAsync());
            // Saved like 首页's volume before the start, and the start uses it: no jump back to 60 % (review of #22).
            Assert.Equal([10.0], host.Applied);
            Assert.Equal(10, host.Config.Volume);
            var (source, speakers, started) = Assert.Single(host.Started);
            Assert.Equal(10, started);
            Assert.Equal([CinemaRole.FrontLeft, CinemaRole.FrontRight], speakers.Select(s => s.Role));
            Assert.Equal(["AA:00:00:00:00:01", "AA:00:00:00:00:02"], speakers.Select(s => s.DeviceId));
            Assert.Equal(6, source.Channels);

            // Another file: the same volume again, never the one saved before the window opened.
            volume.UserSetValue(15);
            wait.Fire();
            Pump(form, form.PlayAsync());
            Assert.Equal(15, host.Started[^1].Volume);
            Assert.Equal(["restart"], host.Stopped);
        }
        finally { File.Delete(wav); }
    });

    [Fact]
    public void Errors_are_shown_in_the_ui_language() => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        string garbage = Path.Combine(Path.GetTempPath(), "hpc-cinema-" + Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 4096).Select(i => (byte)(i * 37)).ToArray());
        try
        {
            var host = new FakeHost();
            using var form = Form(host);

            Pump(form, form.UseFile(wav));
            ChooseFronts(form, right: false);
            Pump(form, form.PlayAsync());
            Assert.Equal("影院播放失败：至少选择完整立体声，或给左和右各选一台音箱。", Status(form));

            ChooseFronts(form);
            Pump(form, form.UseFile(garbage));
            Pump(form, form.PlayAsync());
            Assert.Equal("影院播放失败：这个文件在这台电脑上无法解码：Windows 没有它的解码器，或者文件已损坏。", Status(form));

            Pump(form, form.UseFile(garbage + ".missing"));
            Pump(form, form.PlayAsync());
            Assert.Equal("影院播放失败：选的文件不存在或打不开。", Status(form));
            Assert.Empty(host.Started);
        }
        finally
        {
            File.Delete(wav);
            File.Delete(garbage);
        }
    });

    [Fact]
    public void A_cinema_another_connection_replaced_ends_in_the_window_only() => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            using var form = Form(host);
            ChooseFronts(form);
            Pump(form, form.UseFile(wav));
            Pump(form, form.PlayAsync());
            var calibrate = All<FluentButton>(form).Single(b => b.Text == "测量音箱延迟");

            host.State = CinemaState.Waiting; // connecting, or waiting for a busy speaker
            form.RefreshStatus();
            Assert.Equal("正在连接…", Status(form));
            Assert.False(calibrate.Enabled); // no microphone for a chirp that would never come

            host.State = CinemaState.Playing;
            form.RefreshStatus();
            Assert.StartsWith("正在播放：5.1 · ", Status(form));
            Assert.True(calibrate.Enabled);

            // 连接 on 首页 started the user's own stream: the window lets go of its file, and stopping or closing the window
            // later asks nothing more of the app.
            host.State = CinemaState.Ended;
            form.RefreshStatus();
            Assert.Equal(["replaced"], host.Stopped);
            Assert.Equal("无线影院已停止：音箱改由其他连接或设备使用了。", Status(form));
            Assert.False(calibrate.Enabled);
            form.Close();
            Assert.Equal(["replaced"], host.Stopped);
        }
        finally { File.Delete(wav); }
    });

    private static List<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => (c is T t ? [t] : Array.Empty<T>()).Concat(All<T>(c))).ToList();

    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }
}
