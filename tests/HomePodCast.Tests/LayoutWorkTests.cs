using System.Drawing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;
using HomePodCast.Update;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

// One DPI test makes WinForms follow DPI messages on its own thread (a process-wide switch, set back afterwards): these
// tests never run next to another test.
[CollectionDefinition("Layout work", DisableParallelization = true)]
public sealed class LayoutWorkCollection { }

/// <summary>
/// How much layout work the real pages do (built off-screen with a <see cref="TestApp"/>, in an invisible window): layouts
/// per container, texts measured, windows created. Counts, not times: 2026-10-15 on 1.1.0-beta.5 a toggle in 设置 measured
/// 2188 texts (161 different ones), switching to 混音器 created 70 windows each time, and moving the window to a monitor
/// with another scale laid 设置 out 282 times (8 s 「未响应」). On 1.1.0-beta.7 the first visit to a page created up to 104
/// windows (100–230 ms on the maintainer's PC), and each DPI change sent the new font to all 636 controls of the visited pages
/// and laid the shown page out while Windows waited, once per change of a drag back and forth.
/// </summary>
[Collection("Layout work")]
public class LayoutWorkTests
{
    private readonly ITestOutputHelper output;

    /// <summary>Each test starts with the text cache empty, so that what it counts never meets the cache starting over.</summary>
    public LayoutWorkTests(ITestOutputHelper output)
    {
        this.output = output;
        TextMeasure.Clear();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Windows_UI_Automation_can_read_parent_painted_text_as_text_nodes(bool logical) => Sta.Run(() =>
    {
        var page = new ScrollPage("painted title");
        var status = new TextBlock("current value") { AccessibleName = "status" };
        var button = new FluentButton("push");
        var combo = new FluentComboBox { AccessibleName = "source" };
        combo.Items.AddRange(["live", "file"]); combo.SelectedIndex = 0;
        var nestedStatus = new TextBlock("nested value") { AccessibleName = "nested status" };
        var nestedMetric = new MetricTile("nested count");
        nestedMetric.SetValue("42");
        var nestedRow = Ui.Row(nestedStatus, new GlyphBadge(Glyph.Speaker), nestedStatus);
        var nestedCard = Ui.Card(nestedRow, nestedMetric);
        nestedCard.AccessibleName = "painted card";
        var sourceRow = new FieldRow("source", combo) { AccessibleName = "source row" };
        var mixedToggle = new ToggleSwitch { AccessibleName = "mixed toggle" };
        var mixedCard = Ui.Card(new TextBlock("mixed caption"), mixedToggle);
        mixedCard.AccessibleName = "mixed card";
        page.Content.Controls.AddRange([status, button, sourceRow, nestedCard, mixedCard]);
        if (logical) LogicalLayout.Enable(page.Content);
        using var host = new Host(page, 784);
        Assert.True(host.Page.TitleText!.Visible);
        var hwnd = host.Handle;
        var contentHwnd = host.Page.Content.Handle;
        var buttonHwnd = button.Handle;
        var comboHwnd = combo.Handle;
        var toggleHwnd = mixedToggle.Handle;
        var groupConfig = TestApp.Config();
        groupConfig.GroupSpeakers = [new() { DeviceId = TestApp.DeviceId, Name = "A", Position = SpeakerPosition.Left },
            new() { DeviceId = "AA:00:00:00:00:02", Name = "B", Position = SpeakerPosition.Right }];
        groupConfig.GroupCaptureDeviceIds = [TestApp.DeviceId, "AA:00:00:00:00:02"];
        groupConfig.GroupCapturePositions = new() { [GroupPlan.Key(TestApp.DeviceId)] = SpeakerPosition.Left,
            [GroupPlan.Key("AA:00:00:00:00:02")] = SpeakerPosition.Right };
        using var groupHost = new MainWindow(TestApp.Create(groupConfig));
        TestApp.Field<MixerPage>(groupHost, "_mixer").FindApps = _ => [];
        ShowMain(groupHost);
        Go(groupHost, AppPage.Group);
        var groupPage = TestApp.Field<ScrollPage[]>(groupHost, "_pages")[(int)AppPage.Group];
        var groupHwnd = groupHost.Handle;
        var groupSourceHwnd = All(groupPage).OfType<FluentComboBox>().Single(c => c.AccessibleName == "音源").Handle;
        using var dialogHost = new Host(new ScrollPage("temporary dialog"), 400);
        var dialogHwnd = dialogHost.Handle;
        using var modal = new GroupAlignmentForm([new("A", "A", 0), new("B", "B", 0)], canPlayTest: false)
            { Opacity = 0, ShowInTaskbar = false };
        using var modalShown = new ManualResetEventSlim();
        using var modalClosed = new ManualResetEventSlim();
        IntPtr modalHwnd = IntPtr.Zero;
        modal.Shown += (_, _) => { modalHwnd = modal.Handle; modalShown.Set(); };
        Exception? error = null;
        bool done = false;
        long lastProgress = Environment.TickCount64;
        var reader = new Thread(() =>
        {
            try
            {
                var iid = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
                Assert.Equal(0, AccessibleObjectFromWindow(contentHwnd, 0xFFFFFFFC, ref iid, out var msaa));
                var acc = (Accessibility.IAccessible)msaa;
                output.WriteLine($"MSAA children: {acc.accChildCount}");
                var child = (Accessibility.IAccessible)acc.get_accChild(1);
                output.WriteLine($"MSAA text: {child.get_accName(0)}, role {child.get_accRole(0)}, state {child.get_accState(0)}");
                var request = new System.Windows.Automation.CacheRequest
                {
                    TreeScope = System.Windows.Automation.TreeScope.Subtree,
                    TreeFilter = System.Windows.Automation.Automation.RawViewCondition,
                };
                request.Add(System.Windows.Automation.AutomationElement.NameProperty);
                request.Add(System.Windows.Automation.AutomationElement.ControlTypeProperty);
                request.Add(System.Windows.Automation.AutomationElement.NativeWindowHandleProperty);
                request.Add(System.Windows.Automation.AutomationElement.BoundingRectangleProperty);
                request.Add(System.Windows.Automation.AutomationElement.IsEnabledProperty);
                request.Add(System.Windows.Automation.AutomationElement.ClassNameProperty);
                request.Add(System.Windows.Automation.AutomationElement.AutomationIdProperty);
                System.Windows.Automation.AutomationElement root;
                using (request.Activate()) root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var nodes = new List<(string Name, System.Windows.Automation.ControlType ControlType, int NativeWindowHandle)>();
                void Walk(System.Windows.Automation.AutomationElement node)
                {
                    nodes.Add((node.Cached.Name, node.Cached.ControlType, node.Cached.NativeWindowHandle));
                    foreach (System.Windows.Automation.AutomationElement child in node.CachedChildren) Walk(child);
                }
                Walk(root);
                output.WriteLine(string.Join("\n", nodes.Select(n => $"{n.ControlType.ProgrammaticName}: {n.Name} hwnd={n.NativeWindowHandle}")));
                Assert.Contains(nodes, n => n.Name == "painted title" && n.ControlType == System.Windows.Automation.ControlType.Text && n.NativeWindowHandle == 0);
                Assert.Contains(nodes, n => n.Name == "status" && n.ControlType == System.Windows.Automation.ControlType.Text && n.NativeWindowHandle == 0);
                Assert.Contains(nodes, n => n.Name == "push" && n.ControlType == System.Windows.Automation.ControlType.Button && n.NativeWindowHandle != 0);
                Assert.Equal(buttonHwnd.ToInt32(), nodes.Single(n => n.Name == "push").NativeWindowHandle);
                Assert.Contains(nodes, n => n.Name == "painted card" && n.NativeWindowHandle == 0);
                Assert.Contains(nodes, n => n.Name == "nested status" && n.ControlType == System.Windows.Automation.ControlType.Text && n.NativeWindowHandle == 0);
                var nestedNode = System.Windows.Automation.AutomationElement.FromHandle(contentHwnd)
                    .FindFirst(System.Windows.Automation.TreeScope.Descendants,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "nested count"));
                Assert.NotNull(nestedNode);
                Assert.Equal(0, nestedNode.Current.NativeWindowHandle);
                var metricValue = (System.Windows.Automation.ValuePattern)nestedNode.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern);
                Assert.Equal("42", metricValue.Current.Value);
                host.BeginInvoke(() => nestedMetric.SetValue("43"));
                Assert.True(SpinWait.SpinUntil(() => metricValue.Current.Value == "43", TimeSpan.FromSeconds(3)));
                var sourceNode = nodes.Single(n => n.Name == "source" && n.ControlType == System.Windows.Automation.ControlType.ComboBox);
                Assert.Equal(comboHwnd.ToInt32(), sourceNode.NativeWindowHandle);
                var raw = System.Windows.Automation.TreeWalker.RawViewWalker;
                var sourceParent = raw.GetParent(System.Windows.Automation.AutomationElement.FromHandle(comboHwnd));
                Assert.Equal("source row", sourceParent.Current.Name);
                Assert.Equal(logical ? 0 : sourceRow.Handle.ToInt32(), sourceParent.Current.NativeWindowHandle);
                var toggleNode = System.Windows.Automation.AutomationElement.FromHandle(toggleHwnd);
                Assert.Equal("mixed toggle", toggleNode.Current.Name);
                var toggleParent = raw.GetParent(toggleNode);
                Assert.Equal("mixed card", toggleParent.Current.Name);
                Assert.Equal(logical ? 0 : mixedCard.Handle.ToInt32(), toggleParent.Current.NativeWindowHandle);
                Assert.Equal(1, nodes.Count(n => n.Name == "mixed toggle")); // the HWND placement override must not duplicate it
                var identity = toggleNode.GetRuntimeId();
                Assert.Equal(identity, System.Windows.Automation.AutomationElement.FromHandle(toggleHwnd).GetRuntimeId());
                Assert.Equal(0, AccessibleObjectFromWindow(new IntPtr(sourceNode.NativeWindowHandle), 0xFFFFFFFC, ref iid, out var sourceMsaa));
                ((Accessibility.IAccessible)sourceMsaa).set_accValue(0, "file");
                Assert.Equal("file", ((Accessibility.IAccessible)sourceMsaa).get_accValue(0));
                var statusNode = System.Windows.Automation.AutomationElement.FromHandle(contentHwnd)
                    .FindFirst(System.Windows.Automation.TreeScope.Children,
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "status"));
                Assert.NotNull(statusNode);
                var value = (System.Windows.Automation.ValuePattern)statusNode.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern);
                Assert.True(value.Current.IsReadOnly);
                Assert.Equal("current value", value.Current.Value);
                using (request.Activate()) _ = System.Windows.Automation.AutomationElement.FromHandle(dialogHwnd);
                using (var closed = new ManualResetEventSlim())
                {
                    host.BeginInvoke(() => { dialogHost.Dispose(); closed.Set(); });
                    Assert.True(closed.Wait(TimeSpan.FromSeconds(3)));
                }
                using (request.Activate()) _ = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                Assert.Equal("current value", value.Current.Value); // closing another fragment must not disconnect this one
                // The real unified page mixes many fragments with native controls and rebuilds its rows on source changes.
                var groupRoot = System.Windows.Automation.AutomationElement.FromHandle(groupHwnd);
                var groupSource = groupRoot.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.AndCondition(
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "音源"),
                        new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.ComboBox)));
                Assert.NotNull(groupSource);
                Assert.Equal(groupSourceHwnd.ToInt32(), groupSource.Current.NativeWindowHandle);
                Assert.Equal(0, AccessibleObjectFromWindow(groupSourceHwnd, 0xFFFFFFFC, ref iid, out var groupAcc));
                foreach (var selected in new[] { "本地影音文件", "电脑实时声音" })
                {
                    ((Accessibility.IAccessible)groupAcc).set_accValue(0, selected);
                    Assert.Equal(selected, ((Accessibility.IAccessible)groupAcc).get_accValue(0));
                    using (request.Activate()) _ = System.Windows.Automation.AutomationElement.FromHandle(groupHwnd);
                }
                var positions = groupRoot.FindAll(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Button))
                    .Cast<System.Windows.Automation.AutomationElement>().Where(n => n.Current.Name.StartsWith("前右：") || n.Current.Name.StartsWith("前左："))
                    .ToDictionary(n => n.Current.Name[..2], n => n.Current.NativeWindowHandle);
                string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
                string probeDll = Path.Combine(repo, "tests", "HomePodCast.GuiSmoke", "bin", configuration, "net10.0-windows", "HomePodCast.GuiSmoke.dll");
                var start = new System.Diagnostics.ProcessStartInfo("dotnet")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (var arg in new[] { probeDll, "--uia-probe", groupHwnd.ToInt64().ToString(), groupSourceHwnd.ToInt64().ToString(),
                    positions["前左"].ToString(), positions["前右"].ToString() }) start.ArgumentList.Add(arg);
                using var probe = new System.Diagnostics.Process { StartInfo = start };
                bool ready = false;
                var progress = new System.Collections.Concurrent.ConcurrentQueue<string>();
                probe.OutputDataReceived += (_, e) =>
                {
                    if (e.Data == "READY") Volatile.Write(ref ready, true);
                    if (e.Data == "READY" || e.Data?.StartsWith("PROGRESS:", StringComparison.Ordinal) == true)
                    {
                        progress.Enqueue(e.Data!);
                        Interlocked.Exchange(ref lastProgress, Environment.TickCount64);
                    }
                };
                probe.Start(); probe.BeginOutputReadLine();
                Interlocked.Exchange(ref lastProgress, Environment.TickCount64);
                while (!Volatile.Read(ref ready) && !probe.HasExited && Environment.TickCount64 - Interlocked.Read(ref lastProgress) < 5000) Thread.Sleep(10);
                if (!Volatile.Read(ref ready) && !probe.HasExited) probe.Kill(entireProcessTree: true);
                Assert.True(Volatile.Read(ref ready), "The UIA probe did not become ready.");
                host.BeginInvoke(() => { modal.ShowDialog(groupHost); modalClosed.Set(); });
                Assert.True(modalShown.Wait(TimeSpan.FromSeconds(3)));
                probe.StandardInput.WriteLine(modalHwnd.ToInt64()); probe.StandardInput.Flush();
                Interlocked.Exchange(ref lastProgress, Environment.TickCount64);
                while (!probe.HasExited && Environment.TickCount64 - Interlocked.Read(ref lastProgress) < 5000) Thread.Sleep(10);
                bool exited = probe.HasExited;
                if (!exited) probe.Kill(entireProcessTree: true);
                output.WriteLine(string.Join("\n", progress));
                output.WriteLine(probe.StandardError.ReadToEnd());
                Assert.True(exited, "The UIA probe made no completed-operation progress for 5 seconds.");
                Assert.Equal(0, probe.ExitCode);
            }
            catch (Exception ex) { error = ex; }
            finally { Volatile.Write(ref done, true); }
        }) { IsBackground = true };
        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var completion = new System.Windows.Forms.Timer { Interval = 10 };
        completion.Tick += (_, _) =>
        {
            if (Volatile.Read(ref done) || watch.ElapsedMilliseconds >= 60_000
                || Environment.TickCount64 - Interlocked.Read(ref lastProgress) >= 10_000) Application.ExitThread();
        };
        reader.Start();
        completion.Start();
        Application.Run(); // service UIA's COM callbacks with the real message loop, not intermittent DoEvents
        completion.Stop();
        Assert.True(Volatile.Read(ref done), "UIA exceeded its completed-operation or overall budget");
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    });

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint objectId, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out object accessible);

    [Fact]
    public void Every_page_prepares_text_without_windows_including_dynamic_mixer_rows() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        var mixer = TestApp.Field<MixerPage>(window, "_mixer");
        mixer.FindApps = _ => [new AppAudio(9001, false, "Game", null) { ExeKey = "game.exe" }];
        ShowMain(window);
        int saved = 0, savedVisuals = 0;
        foreach (var page in Enum.GetValues<AppPage>())
        {
            Go(window, page);
            var shown = TestApp.Field<ScrollPage[]>(window, "_pages")[(int)page];
            var texts = All(shown).OfType<TextBlock>().Where(t => t.ParentPainted).ToArray();
            saved += texts.Length;
            var visuals = All(shown).Where(ParentPainting.IsPainted).ToArray();
            savedVisuals += visuals.Length - texts.Length;
            output.WriteLine($"{page}: {texts.Length} windowless texts, {visuals.Length - texts.Length} other windowless visuals/containers, {Handles(shown)} windows");
            Assert.All(visuals, visual => Assert.False(visual.IsHandleCreated, visual.GetType().Name));
            Assert.NotEmpty(texts);
            Assert.All(texts, text => Assert.False(text.IsHandleCreated, $"{page}: 「{text.Text}」 created a window"));
        }
        Assert.True(saved >= 140, $"only {saved} text windows removed");
        output.WriteLine($"Additional windowless visuals/containers: {savedVisuals}");
        Assert.True(savedVisuals >= 25, $"only {savedVisuals} additional windows removed");
        window.Hide();
    });

    [Fact]
    public void Mixer_mixed_rows_are_windowless_and_all_inputs_keep_real_windows_on_the_surface() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        var mixer = TestApp.Field<MixerPage>(window, "_mixer");
        mixer.FindApps = _ => [new AppAudio(9001, false, "Game", null) { ExeKey = "game.exe" }];
        ShowMain(window);
        Go(window, AppPage.Mixer);
        var panels = All(mixer).OfType<LayoutPanel>().Where(panel => panel != mixer.Content).ToArray();
        var inputs = All(mixer).Where(control => !ParentPainting.IsPainted(control) && control is FluentControl).ToArray();
        output.WriteLine($"Mixer: {panels.Length} windowless layout nodes, {inputs.Length} native inputs, {Handles(mixer)} HWNDs");
        Assert.Equal(19, panels.Length); // destination choices, summaries and collapsed details are all windowless
        Assert.All(panels, panel => { Assert.True(panel.ParentPainted); Assert.False(panel.IsHandleCreated); });
        Assert.Equal(13, inputs.Length); // switch-all picker/action and identity action add three native inputs
        Assert.All(inputs, input =>
        {
            Assert.True(input.IsHandleCreated);
            Assert.Same(mixer.Content, input.Parent);
            // The identity action is intentionally a direct page child; nested inputs are reparented to the surface.
            if (input.Text != L.T("确认设备身份")) Assert.NotSame(input.Parent, LogicalLayout.Parent(input));
        });
        Assert.Equal(2 + inputs.Length, Handles(mixer)); // only the scrolling page, native surface and its inputs
        var before = new MixerPage(TestApp.Create(TestApp.Config()), logicalLayout: false);
        before.FindApps = mixer.FindApps;
        using var beforeHost = new Host(before, 784);
        Assert.Equal(Handles(mixer) + 10, Handles(before)); // the new destination row and its actions are also windowless
        mixer.FindApps = _ => [new AppAudio(9001, false, "Game", null) { ExeKey = "game.exe" },
            new AppAudio(9002, false, "Browser", null) { ExeKey = "browser.exe" }];
        using (var work = new Work())
        {
            typeof(MixerPage).GetMethod("RefreshApps", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(mixer, null);
            work.AssertOnce("mixer sessions changed", output);
            Assert.Equal(1, work.Pages);
        }
        Assert.Equal(20, Handles(mixer)); // a second app adds its five input HWNDs, no row window
        window.Hide();
    });

    [Theory]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Mixer_with_logical_rows_matches_the_native_page_pixel_for_pixel(string theme) => Sta.Run(() =>
    {
        var previous = Theme.Mode;
        try
        {
            Theme.Mode = theme;
            MixerPage Make(bool logical)
            {
                var page = new MixerPage(TestApp.Create(TestApp.Config()), logical);
                page.FindApps = _ => [new AppAudio(9001, false, "Game", null) { ExeKey = "game.exe" }];
                return page;
            }
            using var beforeHost = new Host(Make(false), 784);
            using var afterHost = new Host(Make(true), 784);
            var before = beforeHost.Page;
            var after = afterHost.Page;
            Assert.Equal(before.Size, after.Size);
            using var expected = new Bitmap(before.Width, before.Height);
            using var actual = new Bitmap(after.Width, after.Height);
            before.DrawToBitmap(expected, new Rectangle(Point.Empty, expected.Size));
            after.DrawToBitmap(actual, new Rectangle(Point.Empty, actual.Size));
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.True(expected.GetPixel(x, y) == actual.GetPixel(x, y), $"{theme}: pixel {x},{y} differs");
        }
        finally { Theme.Mode = previous; }
    });

    [Fact]
    public void Home_texts_keep_their_layout_and_accessibility_without_windows() => Sta.Run(() =>
    {
        using var host = new Host(new HomePage(TestApp.Create(TestApp.Config())), 784);
        var texts = All(host.Page).OfType<TextBlock>().ToArray();
        var painted = texts.Where(t => t.ParentPainted).ToArray();
        output.WriteLine($"首页: {painted.Length} parent-painted texts, {Handles(host.Page)} native windows");
        Assert.True(painted.Length >= 20, $"only {painted.Length} texts moved to their containers");
        Assert.All(painted, t => Assert.False(t.IsHandleCreated, $"「{t.Text}」 created a window"));
        Assert.True(LayoutPanel.CreateWindows(host.Page, () => false)); // preparation must not undo the saving
        Assert.All(painted, t => Assert.False(t.IsHandleCreated));
        foreach (var text in painted.Where(t => t.Visible && !t.Collapsed && t.Height > 0))
        {
            var parent = text.Parent!.AccessibilityObject;
            var node = text.AccessibilityObject;
            Assert.Contains(node, Enumerable.Range(0, parent.GetChildCount()).Select(parent.GetChild));
            Assert.Equal(text.AccessibleName ?? text.Text, node.Name);
            Assert.Equal(AccessibleRole.StaticText, node.Role);
            var expectedBounds = text.Bounds;
            for (var ancestor = text.Parent; ancestor != host; ancestor = ancestor!.Parent)
                expectedBounds.Offset(ancestor!.Location);
            Assert.Equal(host.RectangleToScreen(expectedBounds), node.Bounds);
            Assert.Same(parent, node.Parent);
            Assert.False(node.State.HasFlag(AccessibleStates.Focusable));
            Assert.Equal(host.Page.DeviceDpi, text.DeviceDpi);
        }
        Assert.All(painted, text => Assert.False(text.IsHandleCreated));
        var status = TestApp.Field<TextBlock>(host.Page, "_status");
        using (var work = new Work())
        {
            status.Text = "A much longer status that is wrapped at the available width";
            work.AssertOnce("parent-painted status change", output);
        }
        Assert.Equal(status.Text, status.AccessibilityObject.Name);
        Assert.Equal(status.GetPreferredSize(new Size(status.Width, 0)).Height, status.Height);
        status.Collapsed = true;
        Assert.DoesNotContain(status.AccessibilityObject, Enumerable.Range(0, status.Parent!.AccessibilityObject.GetChildCount())
            .Select(status.Parent.AccessibilityObject.GetChild));
        status.Collapsed = false;
        Assert.True(status.Visible);
        Assert.False(status.IsHandleCreated);
        Assert.All(All(host.Page).OfType<FluentButton>().Where(b => b.Visible), b => Assert.True(b.IsHandleCreated));
    });

    [Fact]
    public void Home_mixed_rows_are_windowless_while_inputs_stay_on_the_native_surface() => Sta.Run(() =>
    {
        using var before = new Host(new HomePage(TestApp.Create(TestApp.Config()), logicalLayout: false), 784);
        using var after = new Host(new HomePage(TestApp.Create(TestApp.Config())), 784);
        var page = after.Page;
        LayoutPanel.CreateWindows(before.Page, () => false);
        LayoutPanel.CreateWindows(page, () => false);
        var panels = All(page).OfType<LayoutPanel>().Where(panel => panel != page.Content).ToArray();
        var inputs = All(page).Where(control => LogicalLayout.IsInput(control) && control.IsHandleCreated).ToArray();
        output.WriteLine($"Home: {Handles(before.Page)} -> {Handles(page)} HWNDs, {panels.Length} logical layout nodes, {inputs.Length} native inputs");
        Assert.Equal(29, Handles(before.Page));
        Assert.Equal(16, Handles(page));
        Assert.Equal(34, panels.Length);
        Assert.Equal(14, inputs.Length);
        Assert.All(panels, panel => { Assert.True(panel.ParentPainted); Assert.False(panel.IsHandleCreated); });
        Assert.All(inputs, input => Assert.Same(page.Content, input.Parent));
        Assert.Equal(2 + inputs.Length, Handles(page));
        AssertNoOverlap(page.Content);
    });

    [Theory]
    [InlineData(Theme.Light, 784)]
    [InlineData(Theme.Dark, 784)]
    [InlineData(Theme.Light, 600)]
    [InlineData(Theme.Dark, 600)]
    public void Home_with_logical_rows_matches_the_native_page_pixel_for_pixel(string theme, int width) => Sta.Run(() =>
    {
        var previous = Theme.Mode;
        try
        {
            Theme.Mode = theme;
            using var beforeHost = new Host(new HomePage(TestApp.Create(TestApp.Config()), logicalLayout: false), width);
            using var afterHost = new Host(new HomePage(TestApp.Create(TestApp.Config())), width);
            var before = beforeHost.Page;
            var after = afterHost.Page;
            Assert.Equal(before.Size, after.Size);
            using var expected = new Bitmap(before.Width, before.Height);
            using var actual = new Bitmap(after.Width, after.Height);
            before.DrawToBitmap(expected, new Rectangle(Point.Empty, expected.Size));
            after.DrawToBitmap(actual, new Rectangle(Point.Empty, actual.Size));
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.True(expected.GetPixel(x, y) == actual.GetPixel(x, y), $"{theme}/{width}: pixel {x},{y} differs");
        }
        finally { Theme.Mode = previous; }
    });

    [Fact]
    public void Settings_mixed_rows_have_no_windows_and_keep_all_inputs_on_the_surface() => Sta.Run(() =>
    {
        using var before = new Host(new SettingsPage(TestApp.Create(TestApp.Config()), logicalLayout: false), 784);
        using var after = new Host(new SettingsPage(TestApp.Create(TestApp.Config())), 784);
        LayoutPanel.CreateWindows(before.Page, () => false);
        LayoutPanel.CreateWindows(after.Page, () => false);
        var panels = All(after.Page).OfType<LayoutPanel>().Where(panel => panel != after.Page.Content).ToArray();
        var inputs = All(after.Page).Where(control => LogicalLayout.IsInput(control) && control.IsHandleCreated).ToArray();
        output.WriteLine($"Settings: {Handles(before.Page)} -> {Handles(after.Page)} HWNDs, {panels.Length} logical panels, {inputs.Length} created native inputs");
        Assert.Equal(21, Handles(before.Page));
        Assert.Equal(11, Handles(after.Page));
        Assert.Equal(13, panels.Length);
        Assert.Equal(9, inputs.Length);
        Assert.All(panels, panel => { Assert.True(panel.ParentPainted); Assert.False(panel.IsHandleCreated); });
        Assert.All(inputs, input => Assert.Same(after.Page.Content, input.Parent));
        Assert.Equal(2 + inputs.Length, Handles(after.Page));
        AssertNoOverlap(after.Page.Content);
    });

    [Theory]
    [InlineData(Theme.Light, 784)]
    [InlineData(Theme.Dark, 784)]
    [InlineData(Theme.Light, 600)]
    [InlineData(Theme.Dark, 600)]
    public void Settings_with_logical_rows_matches_the_native_page_pixel_for_pixel(string theme, int width) => Sta.Run(() =>
    {
        var previous = Theme.Mode;
        try
        {
            Theme.Mode = theme;
            using var beforeHost = new Host(new SettingsPage(TestApp.Create(TestApp.Config()), logicalLayout: false), width);
            using var afterHost = new Host(new SettingsPage(TestApp.Create(TestApp.Config())), width);
            var before = beforeHost.Page;
            var after = afterHost.Page;
            Assert.Equal(before.Size, after.Size);
            using var expected = new Bitmap(before.Width, before.Height);
            using var actual = new Bitmap(after.Width, after.Height);
            before.DrawToBitmap(expected, new Rectangle(Point.Empty, expected.Size));
            after.DrawToBitmap(actual, new Rectangle(Point.Empty, actual.Size));
            for (int y = 0; y < expected.Height; y++)
            for (int x = 0; x < expected.Width; x++)
                Assert.True(expected.GetPixel(x, y) == actual.GetPixel(x, y), $"{theme}/{width}: pixel {x},{y} differs");
        }
        finally { Theme.Mode = previous; }
    });

    [Fact]
    public void Toggling_night_disconnect_lays_each_container_out_once_and_measures_each_text_once() => Sta.Run(() =>
    {
        var config = TestApp.Config();
        using var host = new Host(new SettingsPage(TestApp.Create(config)), 784);
        var page = (SettingsPage)host.Page;
        var night = TestApp.Field<ToggleSwitch>(page, "_night");
        var nightRow = (SettingRow)LogicalLayout.Parent(night)!;
        var windowRow = TestApp.Field<SettingRow>(page, "_nightWindowRow");
        var idleRow = TestApp.Field<SettingRow>(page, "_nightIdleRow");
        Assert.True(windowRow.Collapsed && idleRow.Collapsed);

        using (var work = new Work())
        {
            night.Flip(); // on: the two rows show for the first time
            work.AssertOnce("夜间自动断开 on", output);
        }
        Assert.True(config.NightDisconnect);
        Assert.False(windowRow.Collapsed || idleRow.Collapsed);
        int gap = Theme.Dp(4, page.DeviceDpi);
        Assert.Equal(nightRow.Bottom + gap, windowRow.Top);
        Assert.Equal(windowRow.Bottom + gap, idleRow.Top);
        Assert.Equal(idleRow.Bottom + Theme.Dp(20, page.DeviceDpi), idleRow.Parent!.Controls[idleRow.Parent.Controls.GetChildIndex(idleRow) + 1].Top);

        using (var work = new Work())
        {
            night.Flip(); // off
            work.AssertOnce("夜间自动断开 off", output);
            night.Flip(); // on again
            Assert.Empty(work.Measured); // every text was measured before
        }
        Assert.Equal(nightRow.Bottom + gap, windowRow.Top);
    });

    [Fact]
    public void An_update_found_after_the_page_was_built_shows_its_buttons_in_order() => Sta.Run(() =>
    {
        // The buttons were collapsed when 设置 was built (no update yet), and are shown last to first: WinForms put 查看说明
        // before 更新 when it sorted them after their windows (LayoutPanel.KeepChildOrder).
        var app = TestApp.Create(TestApp.Config());
        using var host = new Host(new AboutPage(app), 784);
        var next = new Release(ReleaseVersion.Parse("9.0.0-beta.1")!, "v9.0.0-beta.1", "https://example.invalid", true, null);
        typeof(TrayApp).GetField("_update", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, next);
        typeof(TrayApp).GetField("_updateStatus", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(app, "发现新版本 9.0.0-beta.1");
        ((AboutPage)host.Page).ShowUpdate();
        Application.DoEvents();
        var buttons = TestApp.Field<UpdateStatusRow>(host.Page, "_updateRow").Buttons.Controls.Cast<Control>().Where(c => !LayoutPanel.IsCollapsed(c)).ToList();
        Assert.Equal(["更新", "查看说明", "跳过此版本"], buttons.Select(b => b.Text));
        Assert.True(buttons[0].Right < buttons[1].Left && buttons[1].Right < buttons[2].Left);
    });

    [Fact]
    public void A_speaker_info_read_lays_out_the_speakers_page_once_each_time_it_shows() => Sta.Run(() =>
    {
        using var host = new Host(new SpeakersPage(TestApp.Create(TestApp.Config())), 784);
        var info = TestApp.Field<SpeakerInfoPanel>(host.Page, "_speakerInfo");
        var refresh = TestApp.Field<FluentButton>(info, "_refresh");
        int changes = 0;
        var cache = TestApp.Field<Net.SpeakerInfoCache>(info, "_cache");
        cache.Changed += _ => Interlocked.Increment(ref changes); // on the thread pool; each one is shown once (posted ShowAll)
        using var work = new Work();
        refresh.PerformClick(); // shows what is known at once, then the read: reading, read
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (cache.Get(TestApp.DeviceId)?.State != Net.SpeakerInfoState.Ready && DateTime.UtcNow < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        for (int i = 0; i < 10; i++)
        {
            Application.DoEvents();
            Thread.Sleep(20);
        }
        int layouts = work.Arranged.GetValueOrDefault(host.Page);
        output.WriteLine($"刷新: {changes} changes, {layouts} layouts of 设置");
        Assert.True(changes >= 1);
        Assert.Equal(1 + changes, layouts); // before: several for each (every changed text asked for one)
    });

    [Fact]
    public void Switching_pages_lays_out_the_shown_page_once_and_keeps_the_mixer_rows() => Sta.Run(() =>
    {
        var app = TestApp.Create(TestApp.Config());
        using var window = new MainWindow(app);
        var mixer = TestApp.Field<MixerPage>(window, "_mixer");
        List<(uint Pid, string Name)> playing = [(9001, "Game"), (9002, "Browser")];
        mixer.FindApps = _ => playing.Select(p => new AppAudio(p.Pid, false, p.Name, null) { ExeKey = p.Name.ToLowerInvariant() + ".exe" }).ToList();
        ShowMain(window);
        AppPage[] all = Enum.GetValues<AppPage>();
        foreach (var page in all) Go(window, page); // every page once: its windows and texts
        Go(window, AppPage.Settings);
        Assert.Equal(2, MixerRows(mixer).Count);

        foreach (var (from, to) in new[] { (AppPage.Settings, AppPage.Home), (AppPage.Home, AppPage.Mixer), (AppPage.Mixer, AppPage.Effects),
                     (AppPage.Effects, AppPage.Settings), (AppPage.Settings, AppPage.Mixer), (AppPage.Mixer, AppPage.Home) })
        {
            Assert.Equal(from, window.CurrentPage);
            var rows = MixerRows(mixer);
            int handles = Handles(window);
            using (var work = new Work())
            {
                Go(window, to);
                work.AssertOnce($"{from} → {to}", output);
                Assert.Equal(1, work.Pages);
            }
            Assert.Equal(handles, Handles(window)); // no window created (or destroyed) for a page shown before
            Assert.Equal(rows, MixerRows(mixer));
        }

        foreach (var page in all)
        {
            if (window.CurrentPage == page) continue;
            int handles = Handles(window);
            using var work = new Work();
            Go(window, page);
            work.AssertOnce($"new page → {page}", output);
            Assert.Equal(1, work.Pages);
            Assert.Equal(handles, Handles(window));
        }

        // Another app plays meanwhile: the rows are built again, once, when the page is shown.
        playing.Add((9003, "Voice chat"));
        Go(window, AppPage.Mixer);
        Assert.Equal(["Game", "Browser", "Voice chat"], MixerRows(mixer).Select(r => r.Controls.OfType<TextBlock>().First().Text));
        window.Hide();
    });

    [Fact]
    public void Moving_the_window_to_another_monitor_s_scale_lays_out_and_rescales_only_the_shown_page() => Sta.Run(() => PerMonitor(() =>
    {
        var app = TestApp.Create(TestApp.Config());
        using var window = new MainWindow(app);
        ShowMain(window);
        foreach (var page in Enum.GetValues<AppPage>()) Go(window, page);
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        var transition = TestApp.Field<ThemeTransition>(window, "_transition");
        int start = window.DeviceDpi, other = start == 96 ? 144 : 96; // 150 % ↔ 100 %, the maintainer's two monitors
        foreach (var page in Enum.GetValues<AppPage>())
        {
            Go(window, page);
            var shown = pages[(int)page];
            foreach (int dpi in new[] { other, start })
            {
                using (var fonts = new FontWatch(window))
                using (var work = new Work())
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    ChangeDpi(window, dpi);
                    long changed = watch.ElapsedMilliseconds;
                    Assert.Empty(work.Arranged); // laid out once the window has settled at its DPI
                    // The hidden pages' windows went before Windows told and moved each of them; built again at idle.
                    Assert.All(pages.Where(p => p != shown), p => Assert.False(p.IsHandleCreated, $"{p.GetType().Name} kept its windows"));
                    watch.Restart();
                    window.FinishDpiChange(); // what the settle timer does
                    output.WriteLine($"{page} → {dpi} dpi: {changed} ms, then {watch.ElapsedMilliseconds} ms laying out {work.Arranged.Count} containers");
                    work.AssertOnce($"{page} → {dpi} dpi", output);
                    Assert.Same(shown, Assert.Single(work.Arranged.Keys.OfType<ScrollPage>()));
                    // The new font reached the rail and the shown page; the hidden pages get it when they are shown.
                    Assert.True(fonts.In(shown) > 0 && fonts.In(TestApp.Field<NavRail>(window, "_rail")) > 0);
                    foreach (var p in pages.Where(p => p != shown)) Assert.True(fonts.In(p) == 0, $"{p.GetType().Name}: {fonts.Describe(p)}");
                }
                Assert.Equal(dpi, window.DeviceDpi);
                if (page == AppPage.Settings)
                {
                    var night = TestApp.Field<ToggleSwitch>(TestApp.Field<SettingsPage>(window, "_settings"), "_night");
                    Assert.Equal(dpi, night.DeviceDpi);
                    Assert.Equal(night.GetPreferredSize(Size.Empty), night.Size); // laid out at the new DPI
                }
                var rail = TestApp.Field<NavRail>(window, "_rail");
                Assert.All(rail.Items, item => Assert.Equal(Theme.Dp(38, dpi), item.Height));
                AssertNoOverlap(shown.Content);
                foreach (var text in All(shown).OfType<TextBlock>().Where(t => t.ParentPainted && t.Visible && t.Height > 0))
                {
                    Assert.False(text.IsHandleCreated);
                    Assert.Equal(dpi, text.DeviceDpi);
                    Assert.Equal(text.GetPreferredSize(new Size(text.Width, 0)).Height, text.Height);
                }
            }
        }
        Assert.Equal(0, transition.Snapshots); // the theme fade never captures the window for a DPI change
        window.Hide();
    }));

    [Fact]
    public void Scale_changes_in_quick_succession_lay_the_window_out_once_at_the_last() => Sta.Run(() => PerMonitor(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        ShowMain(window);
        Go(window, AppPage.Settings);
        int start = window.DeviceDpi, other = start == 96 ? 144 : 96;
        using (var work = new Work())
        {
            // Dragged back and forth over the edge between the monitors: a change each time the window's middle crosses it.
            foreach (int dpi in new[] { other, start, other }) ChangeDpi(window, dpi);
            Assert.Empty(work.Arranged);
            Assert.Empty(work.Measured);
            Assert.True(window.DpiLayoutPending);
            Settle(window);
            work.AssertOnce("three changes", output);
            Assert.Equal(1, work.Pages);
        }
        Assert.Equal(other, window.DeviceDpi);
        var night = TestApp.Field<ToggleSwitch>(TestApp.Field<SettingsPage>(window, "_settings"), "_night");
        Assert.Equal(night.GetPreferredSize(Size.Empty), night.Size);
        Assert.All(TestApp.Field<NavRail>(window, "_rail").Items, item => Assert.Equal(Theme.Dp(38, other), item.Height));
        window.Hide();
    }));

    [Fact]
    public void A_scale_change_during_a_drag_is_laid_out_when_the_drag_ends() => Sta.Run(() => PerMonitor(() =>
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        ShowMain(window);
        int start = window.DeviceDpi, other = start == 96 ? 144 : 96;
        SendMessage(window.Handle, WM_ENTERSIZEMOVE, 0, IntPtr.Zero);
        using (var work = new Work())
        {
            ChangeDpi(window, other);
            PumpFor(MainWindow.DpiSettleMs * 3); // still dragging: the window keeps following the mouse
            Assert.Empty(work.Arranged);
            Assert.True(window.DpiLayoutPending);
            SendMessage(window.Handle, WM_EXITSIZEMOVE, 0, IntPtr.Zero);
            Assert.False(window.DpiLayoutPending);
            work.AssertOnce("drag ended", output);
            Assert.Equal(1, work.Pages);
        }
        Assert.All(TestApp.Field<NavRail>(window, "_rail").Items, item => Assert.Equal(Theme.Dp(38, other), item.Height));
        window.Hide();
    }));

    [Fact]
    public void A_page_hidden_while_the_scale_changed_is_laid_out_at_the_new_scale_when_shown() => Sta.Run(() => PerMonitor(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        TestApp.Field<MixerPage>(window, "_mixer").FindApps = _ => []; // the rows of playing apps are built when shown
        ShowMain(window); // the other pages built ahead at this DPI
        window.PrepareAhead = false; // visited before they are built again at idle
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        int start = window.DeviceDpi, other = start == 96 ? 144 : 96;
        // There and back: a window made in a test has its monitor's DPI, so the pages are checked back at that one.
        ChangeDpi(window, other);
        Settle(window);
        ChangeDpi(window, start);
        Settle(window);
        foreach (var page in Enum.GetValues<AppPage>().Where(p => p != AppPage.Home))
        {
            var root = pages[(int)page];
            Assert.False(root.IsHandleCreated); // its windows went with the change
            using (var fonts = new FontWatch(window))
            using (var work = new Work())
            {
                window.ShowPage(page);
                work.AssertOnce($"→ {page} after {other} and back to {start} dpi", output);
                Assert.Equal(1, work.Pages);
                Assert.True(fonts.In(root) > 0); // the font it missed while hidden
            }
            Application.DoEvents();
            Assert.Equal(start, root.LayoutDpi);
            foreach (var c in All(root).Where(c => c.IsHandleCreated))
            {
                Assert.Equal(start, c.DeviceDpi);
                if (c is ToggleSwitch && c.Visible) Assert.Equal(c.GetPreferredSize(new Size(c.Width, 0)).Height, c.Height);
                if (c is TextBlock { Wrap: true, Visible: true } text && text.Width > 0)
                    Assert.Equal(text.GetPreferredSize(new Size(text.Width, 0)).Height, text.Height); // measured with the new font
            }
            AssertNoOverlap(root.Content);
        }
        window.Hide();
    }));

    [Fact]
    public void Pages_whose_windows_went_with_a_scale_change_are_built_again_at_idle_at_the_new_scale() => Sta.Run(() => PerMonitor(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        TestApp.Field<MixerPage>(window, "_mixer").FindApps = _ => [];
        ShowMain(window);
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        var vlc = TestApp.Field<PasswordBox>(TestApp.Field<ViewingPage>(window, "_viewing"), "_vlcPassword");
        vlc.Password = "typed, not saved yet";
        int start = window.DeviceDpi, other = start == 96 ? 144 : 96;
        ChangeDpi(window, other); // there and back: see A_page_hidden_while_the_scale_changed…
        Settle(window);
        ChangeDpi(window, start);
        Assert.Equal(pages.Length - 1, pages.Count(p => !p.IsHandleCreated));
        Assert.False(window.PagesPrepared);
        Settle(window);
        Pump(() => window.PagesPrepared, 60_000); // a few milliseconds at a time while the window is idle
        Assert.True(window.LongestPrepareStepMs < 100, $"a step took {window.LongestPrepareStepMs} ms");
        Assert.Equal("typed, not saved yet", vlc.Password);
        foreach (var page in Enum.GetValues<AppPage>().Where(p => p != AppPage.Home))
        {
            var root = pages[(int)page];
            Assert.True(root.IsHandleCreated);
            Assert.Equal(start, root.LayoutDpi);
            int handles = Handles(window);
            using (var work = new Work())
            {
                window.ShowPage(page);
                Assert.True(work.Pages <= 1, $"{page}: laid out {work.Pages} times");
            }
            Assert.Equal(handles, Handles(window)); // shown as built
            Assert.All(All(root).Where(c => c.IsHandleCreated), c => Assert.Equal(start, c.DeviceDpi));
            AssertNoOverlap(root.Content);
        }
        window.Hide();
    }));

    [Theory]
    [InlineData(false, false)] // windows at 96 dpi everywhere: laid out before their windows are created
    [InlineData(true, false)]  // per-monitor, as the app, its controls made at the window's DPI (the window on the main monitor): the same
    [InlineData(true, true)]   // per-monitor, its controls made at another DPI: their windows first, to learn the window's
    public void Pages_built_ahead_show_without_creating_windows_and_with_one_layout(bool perMonitor, bool otherDpi) => Sta.Run(() =>
    {
        void Body()
        {
            var app = TestApp.Create(TestApp.Config());
            // Controls are made at the main monitor's DPI (here the test process's: 96) and take their window's when it is
            // created. The two differ when the window is on the other monitor (2026-10-16: 100 % next to a 150 % main monitor),
            // and for the hidden pages after each move across. Set here, so that both cases run on every PC: until 2026-10-16
            // the second ran only where the off-screen window isn't at 96 dpi (the maintainer's PC, not CI).
            int dpi = perMonitor ? WindowDpi() : 96, made = otherDpi ? (dpi == 96 ? 144 : 96) : dpi;
            using var window = perMonitor ? WithSystemDpi(made, () => new MainWindow(app)) : new MainWindow(app);
            TestApp.Field<MixerPage>(window, "_mixer").FindApps = _ => []; // the rows of playing apps are built when shown
            ShowHidden(window);
            var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
            if (perMonitor)
            {
                Assert.Equal(dpi, window.DeviceDpi);
                Assert.All(pages.Where(p => !p.IsHandleCreated), p => Assert.Equal(made, p.DeviceDpi));
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using (var work = new Work())
            {
                Pump(() => window.PagesPrepared, 60_000);
                output.WriteLine($"built ahead in {watch.ElapsedMilliseconds} ms: {window.PrepareSteps} steps, the longest {window.LongestPrepareStepMs} ms, " +
                                 $"{Handles(window)} windows; " +
                                 string.Join(", ", work.Arranged.Keys.OfType<ScrollPage>().Select(p => $"{p.GetType().Name} ×{work.Arranged[p]}")));
                // The shown page and the rail are left alone, and each page is laid out once, where and as it will show (what a
                // page builds from the app's state, 音箱信息's cards or 多音箱's editor, lays itself out as it is built).
                Assert.DoesNotContain(pages[(int)AppPage.Home], work.Arranged.Keys);
                Assert.DoesNotContain(TestApp.Field<NavRail>(window, "_rail"), work.Arranged.Keys);
                Assert.All(pages, p => Assert.True(work.Arranged.GetValueOrDefault(p) <= 1, $"{p.GetType().Name} ×{work.Arranged.GetValueOrDefault(p)}"));
                // Laid out before their windows were created, never with them: a layout that moves a page's windows is one step
                // that can't yield (2026-10-16, controls at another DPI: 15–45 ms a page on the maintainer's PC, 75 ms while it was
                // busy). Counted, as the time a step takes depends on how busy the PC is.
                Assert.True(work.WithWindows.Count == 0, "laid out with their windows: " +
                    string.Join(", ", work.WithWindows.Select(c => c.GetType().Name).Distinct().Take(10)));
            }
            foreach (var page in Enum.GetValues<AppPage>().Where(p => p != AppPage.Home))
            {
                var root = pages[(int)page];
                Assert.False(root.Created); // never shown
                int handles = Handles(window);
                using (var work = new Work())
                {
                    watch.Restart();
                    window.ShowPage(page);
                    output.WriteLine($"first visit → {page}: {watch.ElapsedMilliseconds} ms, {work.Arranged.Count} containers laid out, " +
                                     $"{work.Measured.Count} texts measured");
                    Assert.Equal(handles, Handles(window));
                    work.AssertOnce($"first visit → {page}", output);
                    Assert.Equal(1, work.Pages);
                }
                Application.DoEvents();
                Assert.Equal(window.DeviceDpi, root.LayoutDpi);
                AssertNoOverlap(root.Content);
            }
            window.Hide();
        }
        if (perMonitor) PerMonitor(Body);
        else Body();
    });

    [Fact]
    public void A_window_started_in_the_tray_builds_no_page_ahead_until_it_is_shown() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        _ = window.Handle; // as TrayApp does, for BeginInvoke; the window stays hidden
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        PumpFor(MainWindow.PrepareStartMs * 3);
        Assert.Equal(0, window.PrepareSteps);
        Assert.All(pages, p => Assert.False(p.IsHandleCreated));
        ShowHidden(window);
        Pump(() => window.PagesPrepared, 60_000);
        Assert.All(pages, p => Assert.True(p.IsHandleCreated));
        window.Hide();
    });

    [Fact]
    public void Building_pages_ahead_waits_while_the_user_uses_the_window() => Sta.Run(() =>
    {
        const int WM_MOUSEMOVE = 0x0200;
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        void Move() => PostMessage(window.Handle, WM_MOUSEMOVE, 0, (IntPtr)(20 | 20 << 16));
        // The mouse moves before the first step can come. When showing the window started the building ahead, a window that took
        // longer than PrepareStartMs to show and paint took a step before the test's first mouse move, and rightly so: idle, no
        // input yet. 2026-10-16 on the maintainer's PC: 1 step, in a full test run and alone. Showing used 150 of the 400 ms there
        // while the PC was quiet; 420 ms more before the first message loop gave that step every time.
        ShowMain(window, prepare: false);
        Move();
        window.PrepareAhead = true;
        window.ShowPage(AppPage.Home); // starts it, as showing the window does
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        var until = Environment.TickCount64 + MainWindow.PrepareStartMs + 1000;
        while (Environment.TickCount64 < until) // the mouse moves over the window: a move before each pass of the message loop
        {
            Application.DoEvents();
            Thread.Sleep(5);
            Move();
        }
        Assert.Equal(0, window.PrepareSteps);
        Assert.All(pages.Where(p => p != pages[(int)AppPage.Home]), p => Assert.False(p.IsHandleCreated));
        Pump(() => window.PagesPrepared, 60_000); // and goes on once it stops
        window.Hide();
    });

    [Theory]
    [InlineData(Theme.Auto)]
    [InlineData(Theme.Light)]
    [InlineData(Theme.Dark)]
    public void Theme_action_updates_preferences_once_and_does_not_navigate(string mode) => Sta.Run(() =>
    {
        string previous = Theme.Mode;
        try
        {
            Theme.Mode = mode;
            var config = TestApp.Config(); config.Theme = mode;
            using var window = new MainWindow(TestApp.Create(config));
            ShowMain(window);
            Go(window, AppPage.Settings);
            var expected = Theme.IsDark ? Theme.Light : Theme.Dark;
            using (var work = new Work())
            {
                window.ToggleTheme();
                Application.DoEvents();
                work.AssertOnce("theme action", output);
                Assert.Equal(1, work.Pages);
            }
            Assert.Equal(expected, config.Theme);
            Assert.Equal(expected, Theme.Mode);
            Assert.Equal(AppPage.Settings, window.CurrentPage);
            var settings = TestApp.Field<SettingsPage>(window, "_settings");
            Assert.Equal(expected == Theme.Light ? 1 : 2, TestApp.Field<FluentComboBox>(settings, "_theme").SelectedIndex);
            Assert.False(TestApp.Field<ThemeTransition>(window, "_transition").Running); // synthetic transparent window
        }
        finally { Theme.Mode = previous; }
    });

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// The window on a monitor at another scale than the main one (2026-10-15: 100 % next to a 150 % main monitor). The list
    /// is a window of its own, made at the system DPI (the main monitor's), and WinForms scales its fonts from there to the
    /// monitor it opens on: fonts made at the dropdown's DPI came out two thirds of its size.
    /// </summary>
    [Fact]
    public void A_dropdown_list_has_the_dropdown_s_text_size_when_the_main_monitor_has_another_scale() => Sta.Run(() => PerMonitor(() =>
    {
        using var form = new Form();
        var combo = new FluentComboBox { Width = 300 };
        combo.Items.AddRange(["卧室（HomePod 第二代）", "书房电视（Apple TV 4K 第三代）· Apple TV"]);
        form.Controls.Add(combo);
        ShowHidden(form);
        int dpi = combo.DeviceDpi;
        WithSystemDpi(dpi == 144 ? 96 : 144, () =>
        {
            using var menu = combo.BuildMenu();
            _ = menu.Handle; // made on this monitor, as it opens next to the dropdown
            Assert.Equal(dpi, menu.DeviceDpi);
            foreach (ToolStripItem item in menu.Items)
                Assert.Equal(Theme.FontFor(TextStyle.Body, dpi, item.Text!).Size, item.Font.Size, 2);
        });
    }));

    [Fact]
    public void A_dropdown_list_s_highlight_spans_the_whole_list() => Sta.Run(() => PerMonitor(() =>
    {
        using var form = new Form();
        var combo = new FluentComboBox { Width = 240 };
        combo.Items.AddRange(["平直", "低音增强", "人声增强", "减弱低音", "自定义"]);
        form.Controls.Add(combo);
        ShowHidden(form);
        int dpi = combo.DeviceDpi;
        foreach (int system in new[] { dpi, dpi == 144 ? 96 : 144 }) // also made at the other monitor's scale
        {
            WithSystemDpi(system, () =>
            {
                using var menu = combo.BuildMenu();
                _ = menu.Handle;
                menu.PerformLayout();
                output.WriteLine($"system {system}: menu {menu.Size} items {string.Join(", ", menu.Items.Cast<ToolStripItem>().Select(i => i.Bounds))}");
                Assert.Equal(combo.Width, menu.Width); // the list as wide as the dropdown, not wider
                // The highlight is inset equally on both sides of the entry: the entry spans the list.
                foreach (ToolStripItem item in menu.Items)
                    Assert.Equal((0, menu.ClientSize.Width), (item.Bounds.Left, item.Bounds.Right));
            });
        }
    }));

    /// <summary>The DPI a window made here gets, per-monitor: its monitor's (off-screen: the main monitor's, 2026-10-16).</summary>
    private static int WindowDpi()
    {
        using var probe = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        _ = probe.Handle;
        return probe.DeviceDpi;
    }

    /// <summary><see cref="WithSystemDpi(int, Action)"/>: what <paramref name="body"/> made, its controls at <paramref name="dpi"/>.</summary>
    private static T WithSystemDpi<T>(int dpi, Func<T> body)
    {
        T made = default!;
        WithSystemDpi(dpi, () => { made = body(); });
        return made;
    }

    /// <summary>
    /// A row hidden in a layout is Collapsed, never only Visible = false: the layouts leave out collapsed controls only, so
    /// a merely hidden one kept its height. 2026-10-18, 首页: the empty 「已保存的分组」 row left a blank under the speaker's
    /// name.
    /// </summary>
    [Fact]
    public void Hidden_rows_take_no_space_on_any_page() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        TestApp.Field<MixerPage>(window, "_mixer").FindApps = _ => [];
        ShowMain(window);
        var pages = TestApp.Field<ScrollPage[]>(window, "_pages");
        foreach (var page in Enum.GetValues<AppPage>())
        {
            Go(window, page);
            var hidden = All(pages[(int)page])
                .Where(c => LogicalLayout.Parent(c) is LayoutPanel { Visible: true } && !LayoutPanel.IsShown(c) && !LayoutPanel.IsCollapsed(c) && c.Height > 0)
                .Select(c => $"{c.GetType().Name} in {LogicalLayout.Parent(c)!.GetType().Name} ({c.Height} px)").ToList();
            Assert.True(hidden.Count == 0, $"{page}: hidden but not collapsed: {string.Join(", ", hidden)}");
        }
        window.Hide();
    });

    /// <summary>首页 side by side: the speaker and volume cards end where the scene card does (2026-10-18: 60 px apart).</summary>
    [Fact]
    public void Home_s_columns_end_on_one_line() => Sta.Run(() =>
    {
        using var window = new MainWindow(TestApp.Create(TestApp.Config()));
        ShowMain(window, prepare: false);
        window.ClientSize = new Size(Theme.Dp(1100, window.DeviceDpi), Theme.Dp(800, window.DeviceDpi));
        Go(window, AppPage.Home);
        window.PerformLayout();
        var columns = All(window).OfType<Columns>().First();
        var (left, right) = (columns.Controls[0], columns.Controls[1]);
        Assert.True(left.Right < right.Left, "not side by side at this width");
        var lastLeft = left.Controls.Cast<Control>().Last(c => !LayoutPanel.IsCollapsed(c));
        Assert.Equal(right.Bottom, left.Top + lastLeft.Bottom);
        window.Hide();
    });

    /// <summary>The system DPI WinForms starts each new control at, as when the main monitor has this scale.</summary>
    private static void WithSystemDpi(int dpi, Action body)
    {
        var helper = Type.GetType("System.Windows.Forms.ScaleHelper, System.Windows.Forms.Primitives", throwOnError: true)!;
        var field = helper.GetField("<InitialSystemDpi>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? before = field.GetValue(null);
        field.SetValue(null, dpi);
        try
        {
            body();
        }
        finally
        {
            field.SetValue(null, before);
        }
    }

    private static IEnumerable<Control> All(Control root) => LogicalLayout.Children(root).SelectMany(c => All(c).Prepend(c));

    /// <summary>
    /// Font changes (Control.FontChanged) of the window's font, per control, while it is in use: the pages, cards, rows and
    /// Fluent controls, which take it from the window (a TextBox with a font of its own is rescaled by WinForms itself).
    /// </summary>
    private sealed class FontWatch : IDisposable
    {
        private readonly Dictionary<Control, int> _changes = new();
        private readonly List<(Control Control, EventHandler Handler)> _hooks = [];

        public FontWatch(Control root)
        {
            foreach (var c in All(root).Prepend(root).Where(c => c is FluentControl or LayoutPanel or ScrollPage))
            {
                EventHandler handler = (_, _) => _changes[c] = _changes.GetValueOrDefault(c) + 1;
                c.FontChanged += handler;
                _hooks.Add((c, handler));
            }
        }

        /// <summary>Changes in <paramref name="root"/> and the controls in it.</summary>
        public int In(Control root) => _changes.Where(kv => kv.Key == root || root.Contains(kv.Key)).Sum(kv => kv.Value);

        public string Describe(Control root) => string.Join(", ", _changes.Where(kv => kv.Key == root || root.Contains(kv.Key))
            .Select(kv => $"{kv.Key.GetType().Name} in {kv.Key.Parent?.GetType().Name} ×{kv.Value}"));

        public void Dispose()
        {
            foreach (var (control, handler) in _hooks) control.FontChanged -= handler;
        }
    }

    /// <summary>As the app runs (Program: per-monitor v2), on this thread only; the test process itself is not DPI aware.</summary>
    private static void PerMonitor(Action body)
    {
        var helper = Type.GetType("System.Windows.Forms.ScaleHelper, System.Windows.Forms.Primitives", throwOnError: true)!;
        var perMonitor = helper.GetField("s_processPerMonitorAware", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? before = perMonitor.GetValue(null);
        perMonitor.SetValue(null, true);
        try
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            body();
        }
        finally
        {
            perMonitor.SetValue(null, before);
        }
    }

    /// <summary>Layouts per container and texts measured on this thread while it is in use.</summary>
    private sealed class Work : IDisposable
    {
        public readonly Dictionary<Control, int> Arranged = new();
        public readonly List<string> Measured = [];

        /// <summary>The containers laid out while they had a window: a layout that moves the windows in them.</summary>
        public readonly List<Control> WithWindows = [];

        public Work()
        {
            LayoutPanel.Arranged = c =>
            {
                Arranged[c] = Arranged.GetValueOrDefault(c) + 1;
                if (c.IsHandleCreated) WithWindows.Add(c);
            };
            // The font object itself, as the cache keys it: a regular and a bold font can share name and size.
            TextMeasure.Measured = (text, font, size, flags) =>
                Measured.Add($"「{text}」 {font.Name} {font.Style} {font.Size}px (font #{RuntimeHelpers.GetHashCode(font)}) {size} {flags}");
        }

        public int Pages => Arranged.Keys.Count(c => c is ScrollPage);

        /// <summary>No container laid out twice, no text measured twice.</summary>
        public void AssertOnce(string what, ITestOutputHelper output)
        {
            output.WriteLine($"{what}: {Arranged.Count} containers laid out, {Measured.Count} texts measured");
            var twice = Arranged.Where(kv => kv.Value > 1).Select(kv => $"{kv.Key.GetType().Name} ×{kv.Value}").ToList();
            Assert.True(twice.Count == 0, $"{what}: laid out more than once: {string.Join(", ", twice.Take(10))}");
            var again = Measured.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => $"{g.Key} ×{g.Count()}").ToList();
            Assert.True(again.Count == 0, $"{what}: measured more than once: {string.Join("; ", again.Take(10))}");
        }

        public void Dispose()
        {
            LayoutPanel.Arranged = null;
            TextMeasure.Measured = null;
        }
    }

    /// <summary>An invisible window (off-screen, transparent) around a page, at a page width in 96-DPI units.</summary>
    private sealed class Host : Form
    {
        public Host(ScrollPage page, int width)
        {
            Page = page;
            Controls.Add(page);
            ShowHidden(this);
            ClientSize = new Size(Theme.Dp(width, DeviceDpi), Theme.Dp(600, DeviceDpi));
            page.SetBounds(0, 0, ClientSize.Width, ClientSize.Height);
            page.PageShown();
            page.PerformLayout();
            Application.DoEvents();
        }

        public ScrollPage Page { get; }
    }

    private static void ShowHidden(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-20000, -20000);
        form.Opacity = 0;
        form.ShowInTaskbar = false;
        form.FormBorderStyle = FormBorderStyle.None;
        form.Show();
        Application.DoEvents();
    }

    /// <summary>The main window shown off-screen, its other pages built ahead (as the app does at idle) unless told not to.</summary>
    private static void ShowMain(MainWindow window, bool prepare = true)
    {
        window.PrepareAhead = prepare;
        ShowHidden(window);
        if (prepare) Pump(() => window.PagesPrepared, 60_000);
    }

    private static void Go(MainWindow window, AppPage page)
    {
        window.ShowPage(page);
        Application.DoEvents(); // what the page posted when shown (the speaker info read)
    }

    /// <summary>Until the window has laid itself out at the DPI it changed to (MainWindow.DpiSettleMs after the last change).</summary>
    private static void Settle(MainWindow window) => Pump(() => !window.DpiLayoutPending, 5000);

    /// <summary>The message loop (timers included) until <paramref name="done"/>.</summary>
    private static void Pump(Func<bool> done, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"still waiting after {timeoutMs} ms");
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void PumpFor(int ms)
    {
        var until = Environment.TickCount64 + ms;
        Pump(() => Environment.TickCount64 >= until, ms + 5000);
    }

    private static List<Control> MixerRows(MixerPage mixer) =>
        TestApp.Field<StackPanel>(mixer, "_rows").Controls.Cast<Control>().ToList();

    private static int Handles(Control root) =>
        (root.IsHandleCreated ? 1 : 0) + root.Controls.Cast<Control>().Sum(Handles);

    private static void AssertNoOverlap(Control root)
    {
        var shown = LogicalLayout.Children(root).Where(c => LayoutPanel.IsShown(c) && c.Width > 0 && c.Height > 0).ToList();
        for (int i = 0; i < shown.Count; i++)
        for (int j = i + 1; j < shown.Count; j++)
            Assert.False(LogicalLayout.Bounds(shown[i]).IntersectsWith(LogicalLayout.Bounds(shown[j])),
                $"{shown[i].GetType().Name} {LogicalLayout.Bounds(shown[i])} and {shown[j].GetType().Name} {LogicalLayout.Bounds(shown[j])} overlap");
        foreach (var child in shown) AssertNoOverlap(child);
    }

    /// <summary>
    /// What Windows does when the window moves to a monitor with another scale, as seen on the maintainer's monitors
    /// (2026-10-15, a hook on every message): WM_GETDPISCALEDSIZE to the window; then to each child window, bottom-up,
    /// WM_DPICHANGED_BEFOREPARENT, and the child moved and resized to the new scale right after; WM_DPICHANGED with the new
    /// rectangle (the same size: WinForms asks for it, AutoScaleMode.None); the window's frame at the new scale, which changes
    /// its client area (here: the window's height); then WM_DPICHANGED_AFTERPARENT top-down.
    /// </summary>
    private static void ChangeDpi(Form window, int dpi)
    {
        int old = window.DeviceDpi;
        var size = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(size, 0, window.Width);
            Marshal.WriteInt32(size, 4, window.Height);
            SendMessage(window.Handle, 0x02E4, dpi, size);
        }
        finally
        {
            Marshal.FreeHGlobal(size);
        }
        var children = new List<Control>();
        void Walk(Control c)
        {
            foreach (Control child in c.Controls)
            {
                if (child.IsHandleCreated) children.Add(child);
                Walk(child);
            }
        }
        Walk(window);
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var child = children[i];
            SendMessage(child.Handle, 0x02E2, dpi, IntPtr.Zero);
            var b = child.Bounds;
            SetWindowPos(child.Handle, IntPtr.Zero, b.X * dpi / old, b.Y * dpi / old, b.Width * dpi / old, b.Height * dpi / old,
                0x0004 | 0x0010); // SWP_NOZORDER | SWP_NOACTIVATE
        }
        var bounds = window.Bounds;
        var rect = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(rect, 0, bounds.Left);
            Marshal.WriteInt32(rect, 4, bounds.Top);
            Marshal.WriteInt32(rect, 8, bounds.Right);
            Marshal.WriteInt32(rect, 12, bounds.Bottom);
            SendMessage(window.Handle, 0x02E0, dpi | (dpi << 16), rect);
        }
        finally
        {
            Marshal.FreeHGlobal(rect);
        }
        window.Height += dpi > old ? -17 : 17; // the frame (on screen: 1484×1011 at 96 dpi, 1478×994 at 144)
        foreach (var child in children.Where(c => c.IsHandleCreated)) SendMessage(child.Handle, 0x02E3, 0, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, nint wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, nint wParam, IntPtr lParam);
}
