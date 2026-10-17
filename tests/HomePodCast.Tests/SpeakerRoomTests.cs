using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using HomePodCast.UI.Controls;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class SpeakerRoomTests
{
    private const uint Rear51 = 0x3f;
    private static RoomOutput Output(SpeakerPosition position, string id = "A") => new(id, id, position, 0, 0, true, Enum.GetValues<SpeakerPosition>());

    [Fact]
    public void Floor_positions_follow_the_source_and_standard_angles()
    {
        Assert.Equal([SpeakerPosition.Left, SpeakerPosition.Right, SpeakerPosition.Stereo],
            SpeakerRoomLayout.Positions(CinemaChannels.FL | CinemaChannels.FR, [Output(SpeakerPosition.RearLeft)]));
        var rear = SpeakerRoomLayout.Positions(Rear51, []);
        Assert.Contains(SpeakerPosition.Center, rear); Assert.Contains(SpeakerPosition.Subwoofer, rear);
        Assert.Contains(SpeakerPosition.RearLeft, rear); Assert.DoesNotContain(SpeakerPosition.SideLeft, rear);
        Assert.Equal(-30, SpeakerRoomLayout.Angle(SpeakerPosition.Left, false));
        Assert.Equal(30, SpeakerRoomLayout.Angle(SpeakerPosition.Right, false));
        Assert.Equal(0, SpeakerRoomLayout.Angle(SpeakerPosition.Center, true));
        Assert.Equal(-110, SpeakerRoomLayout.Angle(SpeakerPosition.RearLeft, false));
        Assert.Equal(-100, SpeakerRoomLayout.Angle(SpeakerPosition.SideLeft, true));
        Assert.Equal(145, SpeakerRoomLayout.Angle(SpeakerPosition.RearRight, true));
        Assert.Equal(9, SpeakerRoomLayout.Positions(CinemaChannels.Surround71, []).Length);
        var sub = SpeakerRoomLayout.Location(SpeakerPosition.Subwoofer, true, new Size(280, 360));
        Assert.True(sub.X < 280 * .15 && sub.Y < 360 * .15); // front corner, never an invented listener-distance estimate
    }

    [Fact]
    public void Empty_slots_use_the_actual_mixer_folding_rules()
    {
        RoomOutput[] fronts = [Output(SpeakerPosition.Left), Output(SpeakerPosition.Right, "B")];
        Assert.Equal("未分配：混入前左 / 前右", SpeakerRoomLayout.EmptyText(SpeakerPosition.Center, Rear51, fronts));
        Assert.Equal("未分配：混入前左", SpeakerRoomLayout.EmptyText(SpeakerPosition.RearLeft, Rear51, fronts));
        Assert.Equal("未分配：混入侧左", SpeakerRoomLayout.EmptyText(SpeakerPosition.RearLeft, Rear51,
            [.. fronts, Output(SpeakerPosition.SideLeft, "C")]));
        Assert.Equal("未分配：混入侧左 / 完整立体声", SpeakerRoomLayout.EmptyText(SpeakerPosition.RearLeft, CinemaChannels.Surround71,
            [.. fronts, Output(SpeakerPosition.SideLeft, "C"), Output(SpeakerPosition.Stereo, "D")]));
        Assert.Equal("未分配：混入完整立体声", SpeakerRoomLayout.EmptyText(SpeakerPosition.Center, Rear51, [Output(SpeakerPosition.Stereo)]));
        Assert.Equal("未分配：没有可播放的音箱", SpeakerRoomLayout.EmptyText(SpeakerPosition.Center, Rear51, []));
        Assert.Equal("此音源没有这个声道", SpeakerRoomLayout.EmptyText(SpeakerPosition.SideLeft, Rear51, fronts));
    }

    private static AppConfig Config() => new()
    {
        DeviceId = "A", DeviceName = "客厅", GroupSpeakers =
        [
            new() { DeviceId = "A", Name = "客厅", Position = SpeakerPosition.Left, DelayMs = 24 },
            new() { DeviceId = "B", Name = "卧室", Position = SpeakerPosition.Right },
            new() { DeviceId = "C", Name = "书房", Position = SpeakerPosition.Stereo },
            new() { DeviceId = "D", Name = "厨房", Position = SpeakerPosition.Stereo },
        ],
        GroupCaptureDeviceIds = ["A", "B", "C", "D"],
        GroupCapturePositions = new() { ["A"] = SpeakerPosition.Left, ["B"] = SpeakerPosition.Right,
            ["C"] = SpeakerPosition.Stereo, ["D"] = SpeakerPosition.Stereo },
    };
    private static List<AirPlayDevice> Found(AppConfig config) => config.GroupSpeakers!.Select((s, i) =>
        new AirPlayDevice(s.Name, s.DeviceId, System.Net.IPAddress.Loopback, 7000 + i, "AudioAccessory6,1", new Dictionary<string, string>())).ToList();
    private static IEnumerable<T> All<T>(Control root) where T : Control => root.Controls.Cast<Control>()
        .SelectMany(c => (c is T t ? new[] { t } : []).Concat(All<T>(c)));
    private static FluentComboBox Device(Control panel) => All<FluentComboBox>(panel).Single(c => c.AccessibleName == "所选位置音箱");
    private static FluentComboBox Position(Control panel, string name) => All<FluentComboBox>(panel).Single(c => c.AccessibleName == name + " 位置");
    private static void Choose(FluentComboBox combo, string text) => combo.SelectedIndex = combo.Items.IndexOf(text);

    [Fact]
    public void Diagram_edits_share_the_existing_draft_controls_and_keep_all_full_stereo_outputs() => Sta.Run(() =>
    {
        var config = Config();
        using var panel = new GroupPanel(config);
        panel.SetFound(Found(config));
        var room = All<SpeakerRoom>(panel).Single();
        string before = JsonSerializer.Serialize(config);
        room.SelectPosition(SpeakerPosition.Left);
        Assert.Contains(room.Controls.Cast<Control>(), c => c.AccessibleName == "前左：客厅，延迟 24 毫秒");
        Choose(Device(panel), "书房");
        Assert.Equal("左", Position(panel, "书房").SelectedText);
        All<FluentSlider>(panel).Single(s => s.AccessibleName == "所选位置延迟（毫秒）").UserSetValue(-71);
        All<FluentSlider>(panel).Single(s => s.AccessibleName == "所选位置音量偏移").UserSetValue(-9);
        Assert.Equal(-71, All<FluentSlider>(panel).Single(s => s.AccessibleName == "书房 延迟（毫秒）").Value);
        Assert.Equal(-9, All<FluentSlider>(panel).Single(s => s.AccessibleName == "书房 音量偏移").Value);
        Choose(Device(panel), "不分配（保留完整立体声）");
        Assert.Equal("完整立体声", Position(panel, "客厅").SelectedText);
        Assert.Equal("完整立体声", Position(panel, "书房").SelectedText);
        room.SelectPosition(SpeakerPosition.Stereo);
        Assert.Contains("客厅", Device(panel).Items); Assert.Contains("书房", Device(panel).Items);
        Assert.Contains("厨房", Device(panel).Items);
        Choose(Device(panel), "书房"); // inspecting one full-stereo member cannot replace the others
        Assert.Equal("完整立体声", Position(panel, "厨房").SelectedText);
        Assert.Equal(before, JsonSerializer.Serialize(config)); // selection and sliders never save/apply or start outputs
        panel.Apply();
        Assert.Equal(4, config.GroupCaptureDeviceIds!.Count);
        Assert.Equal(SpeakerPosition.Stereo, config.GroupCapturePositions!["C"]);
        Assert.Equal(-71, config.GroupCaptureDelayTrims["C"]);
    });

    [Fact]
    public void File_role_reassignment_preserves_devices_and_stereo_sources_preserve_inactive_surrounds() => Sta.Run(() =>
    {
        var config = Config();
        config.GroupSpeakers![2].Position = SpeakerPosition.RearLeft;
        using var panel = new GroupPanel(config);
        panel.SetFound(Found(config));
        panel.SetSource(true, fileSource: true, mask: Rear51);
        var room = All<SpeakerRoom>(panel).Single();
        room.SelectPosition(SpeakerPosition.Left);
        Choose(Device(panel), "卧室");
        Assert.Equal("左", Position(panel, "卧室").SelectedText);
        Assert.Equal("完整立体声", Position(panel, "客厅").SelectedText);
        panel.SetSource(false, fileSource: true, mask: CinemaChannels.FL | CinemaChannels.FR);
        Assert.Equal(3, room.Controls.Count);
        Assert.Equal("后左", Position(panel, "书房").SelectedText);
        Assert.False(Position(panel, "书房").Enabled);
        room.SelectPosition(SpeakerPosition.Left);
        Assert.DoesNotContain("书房", Device(panel).Items);
        panel.SetSource(true, fileSource: true, mask: Rear51);
        Assert.Contains(room.Controls.Cast<Control>(), c => c.AccessibleName!.Contains("后左：书房"));
        Assert.Equal("后左", Position(panel, "书房").SelectedText);
    });

    [Fact]
    public void Keyboard_accessibility_and_measurement_highlight_are_inert_and_editing_freezes_during_playback() => Sta.Run(() =>
    {
        var config = Config();
        using var panel = new GroupPanel(config);
        panel.SetFound(Found(config));
        var room = All<SpeakerRoom>(panel).Single();
        string before = JsonSerializer.Serialize(config);
        var left = room.Controls.Cast<Control>().Single(c => c.AccessibleName!.StartsWith("前左："));
        Assert.Equal(AccessibleRole.PushButton, left.AccessibilityObject.Role);
        Assert.True(left.TabStop);
        var key = new KeyEventArgs(Keys.Right);
        left.GetType().GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(left, [key]);
        Assert.Equal(SpeakerPosition.Right, room.SelectedPosition);
        Assert.True(key.Handled);
        panel.ShowMeasuringSpeaker("B");
        Assert.Contains(room.Controls.Cast<Control>(), c => c.AccessibleName == "前右：卧室，延迟 0 毫秒：正在测量");
        panel.SetPlaybackActive(true);
        Assert.False(Device(panel).Enabled);
        room.SelectPosition(SpeakerPosition.Left);
        Assert.False(All<FluentSlider>(panel).Single(s => s.AccessibleName == "所选位置延迟（毫秒）").Enabled);
        panel.ShowMeasuringSpeaker(null);
        Assert.DoesNotContain(room.Controls.Cast<Control>(), c => c.AccessibleName!.EndsWith("正在测量"));
        Assert.Equal(before, JsonSerializer.Serialize(config));
    });

    [Theory]
    [InlineData(280)]
    [InlineData(560)]
    public void Diagram_positions_fit_without_overlapping_in_narrow_or_wide_layouts(int width) => Sta.Run(() =>
    {
        using var host = new Panel();
        using var room = new SpeakerRoom();
        host.Controls.Add(room);
        room.Update(CinemaChannels.Surround71, [], null);
        room.Size = new Size(width, room.GetPreferredSize(new Size(width, 0)).Height);
        room.PerformLayout();
        var children = room.Controls.Cast<Control>().ToArray();
        Assert.All(children, button => Assert.True(room.ClientRectangle.Contains(button.Bounds), button.AccessibleName));
        foreach (var (a, index) in children.Select((button, index) => (button, index)))
            Assert.All(children.Skip(index + 1), b => Assert.False(a.Bounds.IntersectsWith(b.Bounds), $"{a.AccessibleName} overlaps {b.AccessibleName}"));
        var center = SpeakerRoomLayout.Listener(room.ClientSize);
        foreach (var (name, degrees) in new[] { ("前左", -30), ("前右", 30), ("中置", 0), ("侧左", -100), ("后右", 145) })
        {
            var button = children.Single(c => c.AccessibleName!.StartsWith(name + "："));
            double x = button.Left + button.Width / 2.0 - center.X, y = center.Y - button.Top - button.Height / 2.0;
            Assert.InRange(Math.Atan2(x, y) * 180 / Math.PI, degrees - 1, degrees + 1);
        }
    });
}
