using System.Net;
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
    public void A_file_opening_in_the_background_cannot_start_after_leaving_its_source(bool hidePage) => OnSta(() =>
    {
        string wav = CinemaTests.TempWave(16, 6, CinemaTests.Rear51, 48000);
        try
        {
            var host = new FakeHost();
            using var page = new CinemaPage(host, inputs: [], volumeWait: new ManualDebounce());
            page.UseFile(wav);
            var opened = new TaskCompletionSource<bool>();
            page.WaitForFileOpened = _ => opened.Task;
            var start = page.PlayAsync();
            Assert.False(start.IsCompleted);
            if (hidePage) page.PageHidden();
            else All<FluentComboBox>(page).Single(c => c.AccessibleName == "音源").SelectedIndex = 0;
            opened.SetResult(true);
            Pump(start);
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
            form.UseFile(wav);
            Pump(form.PlayAsync());
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
        public IReadOnlyList<AirPlayDevice> Speakers { get; } =
        [
            new("客厅", "AA:00:00:00:00:01", IPAddress.Parse("192.168.50.11"), 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
            new("书房", "AA:00:00:00:00:02", IPAddress.Parse("192.168.50.12"), 7000, "AudioAccessory6,1", new Dictionary<string, string>()),
            new("Apple TV", "AA:00:00:00:00:03", IPAddress.Parse("192.168.50.13"), 7000, "AppleTV14,1", new Dictionary<string, string>()),
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

    private static void Pump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
        Assert.True(task.IsCompleted, "Play did not finish");
        task.GetAwaiter().GetResult();
    }

    private static string Status(Form form) => All<TextBlock>(form).Select(t => t.Text).FirstOrDefault(t =>
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
            form.UseFile(wav);
            volume.UserSetValue(10);
            Assert.Equal([10.0], host.Previewed); // the speakers follow the drag
            Assert.True(wait.Enabled);

            Pump(form.PlayAsync());
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
            Pump(form.PlayAsync());
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

            form.UseFile(wav);
            ChooseFronts(form, right: false);
            Pump(form.PlayAsync());
            Assert.Equal("影院播放失败：至少选择完整立体声，或给左和右各选一台音箱。", Status(form));

            ChooseFronts(form);
            form.UseFile(garbage);
            Pump(form.PlayAsync());
            Assert.Equal("影院播放失败：这个文件在这台电脑上无法解码：Windows 没有它的解码器，或者文件已损坏。", Status(form));

            form.UseFile(garbage + ".missing");
            Pump(form.PlayAsync());
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
            form.UseFile(wav);
            Pump(form.PlayAsync());
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
