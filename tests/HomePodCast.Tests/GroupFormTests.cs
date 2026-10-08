using System.Drawing;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>The experimental multi-speaker dialog, built offscreen with synthetic scan results (never shown).</summary>
public class GroupFormTests
{
    private const string Tsid = "6A1B2C3D-0000-4000-8000-0123456789AB";

    private static AirPlayDevice Device(string name, string id, string ip, params (string Key, string Value)[] txt)
    {
        var t = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["deviceid"] = id };
        foreach (var (k, v) in txt) t[k] = v;
        return new AirPlayDevice(name, id, IPAddress.Parse(ip), 7000, "AudioAccessory6,1", t);
    }

    private static readonly AirPlayDevice A = Device("客厅", "AA:00:00:00:00:01", "192.168.50.11", ("tsid", Tsid), ("gpn", "客厅"));
    private static readonly AirPlayDevice B = Device("客厅 (2)", "AA:00:00:00:00:02", "192.168.50.12", ("tsid", Tsid), ("gpn", "客厅"));
    private static readonly AirPlayDevice Other = Device("书房", "AA:00:00:00:00:09", "192.168.50.30");

    [Fact]
    public void Pair_rows_show_which_speaker_is_left_and_swap_flips_them() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = StereoPairs.PairId(Tsid), DeviceName = "客厅", GroupSplitChannels = true };
        using var form = new GroupForm(cfg);
        form.SetFound([B, Other, A]);
        Snapshot(form, "group-pair");

        Assert.Equal([("左声道", "客厅  192.168.50.11"), ("右声道", "客厅 (2)  192.168.50.12")], Rows(form));
        Assert.False(Find<ComboBox>(form).Enabled); // a pair needs no second speaker

        Find<CheckBox>(form, "交换左右").Checked = true;
        Assert.Equal([("右声道", "客厅  192.168.50.11"), ("左声道", "客厅 (2)  192.168.50.12")], Rows(form));

        Find<CheckBox>(form, "电脑端分声道").Checked = false;
        Assert.All(Rows(form), r => Assert.Equal("完整立体声", r.Role));
        Assert.False(Find<CheckBox>(form, "交换左右").Enabled);

        All<NumericUpDown>(form)[1].Value = -6;
        form.Apply();
        Assert.False(cfg.GroupSplitChannels);
        Assert.True(cfg.GroupSwapChannels);
        Assert.Equal(new Dictionary<string, int> { [GroupPlan.Key(B.DeviceId)] = -6 }, cfg.GroupVolumeOffsets);
        Assert.Null(cfg.MultiRoomDeviceId);
    });

    [Fact]
    public void Pair_with_one_speaker_missing_says_both_must_be_online() => OnSta(() =>
    {
        using var form = new GroupForm(new AppConfig { DeviceId = StereoPairs.PairId(Tsid), DeviceName = "客厅" });
        form.SetFound([A]);
        Snapshot(form, "group-pair-incomplete");
        Assert.Empty(Rows(form));
        Assert.Contains(All<Label>(form), l => l.Text.Contains("两只都在线"));
    });

    [Fact]
    public void Multi_room_offers_other_speakers_and_saves_the_choice() => OnSta(() =>
    {
        var cfg = new AppConfig { DeviceId = "AA:00:00:00:00:05", DeviceName = "卧室", Host = "192.168.50.5" };
        using var form = new GroupForm(cfg);
        form.SetFound([Other, A, B, Device("卧室", "AA:00:00:00:00:05", "192.168.50.5")]);

        var combo = Find<ComboBox>(form);
        Assert.True(combo.Enabled);
        // (不使用), the pair is not offered as a second speaker, the current speaker neither
        Assert.Equal(["（不使用）", "书房  192.168.50.30"], combo.Items.Cast<string>());
        Assert.Empty(Rows(form));
        Assert.False(Find<CheckBox>(form, "电脑端分声道").Enabled);

        combo.SelectedIndex = 1;
        Snapshot(form, "group-multiroom");
        Assert.Equal([("完整立体声", "卧室"), ("完整立体声", "书房")], Rows(form));
        Find<CheckBox>(form, "电脑端分声道").Checked = true;
        Assert.Equal([("左声道", "卧室"), ("右声道", "书房")], Rows(form));

        form.Apply();
        Assert.Equal("AA:00:00:00:00:09", cfg.MultiRoomDeviceId);
        Assert.Equal("书房", cfg.MultiRoomDeviceName);
        Assert.Equal("192.168.50.30", cfg.MultiRoomHost);
        Assert.Equal(GroupKind.MultiRoom, GroupPlan.FromConfig(cfg)!.Kind);

        combo.SelectedIndex = 0;
        form.Apply();
        Assert.Null(cfg.MultiRoomDeviceId);
        Assert.Null(GroupPlan.FromConfig(cfg)); // back to the single-speaker path
    });

    private static List<(string Role, string Name)> Rows(Form form)
    {
        var table = All<TableLayoutPanel>(form).Single(); // the member rows
        var cells = table.Controls.OfType<Label>().Select(l => (Pos: table.GetPositionFromControl(l), l.Text)).ToList();
        return cells.Where(c => c.Pos.Column == 0).OrderBy(c => c.Pos.Row)
            .Select(c => (c.Text, cells.Single(n => n.Pos.Row == c.Pos.Row && n.Pos.Column == 1).Text)).ToList();
    }

    private static T Find<T>(Control root, string? text = null) where T : Control =>
        All<T>(root).Single(c => text == null || c.Text == text);

    private static List<T> All<T>(Control root) where T : Control =>
        root.Controls.Cast<Control>().SelectMany(c => (c is T t ? [t] : Array.Empty<T>()).Concat(All<T>(c))).ToList();

    /// <summary>Renders the form itself (not the screen) to a PNG when HPC_UI_SNAPSHOT_DIR is set.</summary>
    private static void Snapshot(Form form, string name)
    {
        if (Environment.GetEnvironmentVariable("HPC_UI_SNAPSHOT_DIR") is not { Length: > 0 } dir) return;
        // Laid out for real but invisible: fully transparent and off-screen.
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.Show();
        Application.DoEvents();
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
        bmp.Save(Path.Combine(dir, name + ".png"));
        form.Hide();
    }

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
