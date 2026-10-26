using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

[Collection("Layout work")]
public class MixerModeHelpTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Details_fold_without_hiding_summaries_or_the_settings_link(bool logical) => Sta.Run(() =>
    {
        var launched = new List<string>();
        using var parent = new Panel();
        using var host = new StackPanel { Size = new Size(700, 2000) };
        parent.Controls.Add(host);
        var help = new MixerModeHelp { LaunchSettings = launched.Add };
        host.Controls.Add(help);
        if (logical) LogicalLayout.Enable(host);
        LayoutPanel.Place(host, 0, 0, 700, 2000);
        LayoutPanel.CreateWindows(host, () => false);
        var more = All(help).OfType<FluentButton>().Single(b => b.Text == "了解更多");
        var settings = All(help).OfType<FluentButton>().Single(b => b.Text == "打开 Windows 应用音量设置");
        var details = LogicalLayout.Children(help).OfType<StackPanel>().Single(s => s.Collapsed);
        var summary = All(help).OfType<TextBlock>().Single(t => t.Text.StartsWith("为程序单独选择"));
        int height = help.Height;

        CheckCollapsed();
        settings.PerformClick();
        more.PerformClick();
        Assert.False(details.Collapsed);
        Assert.True(help.Height > height);
        Assert.Equal("收起详细说明", more.AccessibilityObject.Name);
        Assert.Contains(details.AccessibilityObject, AccessibleChildren(help));
        Assert.True(settings.Visible && summary.Visible);
        settings.PerformClick();
        more.PerformClick();
        CheckCollapsed();
        Assert.Equal(height, help.Height);
        Assert.Equal([MixerModeHelp.AppVolumeSettings, MixerModeHelp.AppVolumeSettings], launched);

        void CheckCollapsed()
        {
            Assert.True(details.Collapsed);
            Assert.DoesNotContain(details.AccessibilityObject, AccessibleChildren(help));
            Assert.Equal("了解更多", more.AccessibilityObject.Name);
            Assert.Contains($"{RoutedCapture.RoutedExtraLatencyMs} ms", summary.Text);
            Assert.Contains("游戏", summary.Text);
            Assert.True(summary.Visible && settings.Visible && more.TabStop && settings.TabStop);
            Assert.Equal(AccessibleRole.Link, more.AccessibilityObject.Role);
            Assert.Equal(AccessibleRole.Link, settings.AccessibilityObject.Role);
            Assert.DoesNotContain(settings, All(details));
        }
    });

    [Fact]
    public void Space_and_Enter_keep_focus_on_the_help_button_and_tab_reaches_settings() => Sta.Run(() =>
    {
        // An isolated off-screen control fixture, without TrayApp, discovery, timers or audio endpoints.
        using var form = new QuietForm { ClientSize = new Size(700, 600), Location = new Point(-20000, -20000),
            StartPosition = FormStartPosition.Manual, Opacity = 0, ShowInTaskbar = false };
        var host = new StackPanel { Dock = DockStyle.Fill };
        var help = new MixerModeHelp { LaunchSettings = _ => Assert.Fail("only the help button should activate") };
        host.Controls.Add(help);
        form.Controls.Add(host);
        LogicalLayout.Enable(host);
        form.Show();
        LayoutPanel.Place(host, 0, 0, 700, 600);
        LayoutPanel.CreateWindows(host, () => false);
        var more = All(help).OfType<FluentButton>().Single(b => b.Text == "了解更多");
        var settings = All(help).OfType<FluentButton>().Single(b => b.Text == "打开 Windows 应用音量设置");
        Assert.True(more.Focus());
        var handle = more.Handle;
        SendMessage(handle, 0x0100, (IntPtr)Keys.Space, IntPtr.Zero);
        Assert.Equal("收起详细说明", more.Text);
        Assert.True(more.Focused);
        SendMessage(handle, 0x0100, (IntPtr)Keys.Enter, IntPtr.Zero);
        Assert.Equal("了解更多", more.Text);
        Assert.True(more.Focused);
        Assert.Equal(handle, more.Handle);
        Assert.True(host.SelectNextControl(more, forward: false, tabStopOnly: true, nested: true, wrap: false));
        Assert.True(settings.Focused);
        form.Hide();
    });

    private static IEnumerable<Control> All(Control control) =>
        LogicalLayout.Children(control).SelectMany(child => new[] { child }.Concat(All(child)));

    private static IEnumerable<AccessibleObject> AccessibleChildren(Control control) =>
        Enumerable.Range(0, control.AccessibilityObject.GetChildCount()).Select(i => control.AccessibilityObject.GetChild(i)!);

    private sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation => true;
    }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
