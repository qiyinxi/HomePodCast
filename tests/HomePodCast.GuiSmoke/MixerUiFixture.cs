using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.Tests;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.GuiSmoke;

/// <summary>CI-only window hosting the product mixer and real controller stack, with no TrayApp startup or native audio.</summary>
internal static class MixerUiFixture
{
    internal static int Run(string language, string auditFile)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        { Console.Error.WriteLine("The mixer fixture window runs only on an isolated CI runner."); return 2; }
        int exit = 0;
        var thread = new Thread(() =>
        {
            try { Window(language, Path.GetFullPath(auditFile)); }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return exit;
    }

    private static void Window(string language, string auditFile)
    {
        L.Use(language);
        Environment.SetEnvironmentVariable("HOMEPODCAST_PROFILE", "mixer-gui-fixture");
        Directory.CreateDirectory(Path.GetDirectoryName(auditFile)!);
        Log.OpenFile(Path.Combine(Path.GetDirectoryName(auditFile)!, "fixture.log"));
        var scenario = new MixerScenario();
        var app = (TrayApp)RuntimeHelpers.GetUninitializedObject(typeof(TrayApp));
        var initial = new StreamController();
        var routing = new AppRouting(scenario.Config, () => scenario.Save(Path.Combine(Path.GetDirectoryName(auditFile)!, "fixture-config.json")));
        Set(app, "<Config>k__BackingField", scenario.Config); Set(app, "<Controller>k__BackingField", initial);
        Set(app, "_legacyController", initial); Set(app, "<Routing>k__BackingField", routing);
        Set(app, "<MixSources>k__BackingField", new MixSources()); Set(app, "<LastScan>k__BackingField", scenario.Scan.ToList());
        Set(app, "<Mixer>k__BackingField", scenario.Coordinator);
        Set(app, "_mixerEqualizers", new ConcurrentDictionary<StreamController, Equalizer>());
        app.SaveMixerConfiguration = () => scenario.Save(Path.Combine(Path.GetDirectoryName(auditFile)!, "fixture-config.json"));
        scenario.LiveLevels = () => routing.Rules.SendVolumes;
        using var form = new Form { Text = "HomePodCast", AutoScaleMode = AutoScaleMode.None,
            StartPosition = FormStartPosition.CenterScreen, BackColor = Theme.P.Window, Icon = Icons.App };
        form.ClientSize = new Size(Theme.Dp(900, form.DeviceDpi), Theme.Dp(610, form.DeviceDpi));
        var page = new MixerPage(app) { Dock = DockStyle.Fill };
        page.FindCaptureOutputs = () => [new AudioEndpoint(MixerScenario.NativeId, "CI fake Windows default", 0),
            new AudioEndpoint(MixerScenario.CaptureId, "CI fake capture output", 0)];
        page.IsLocalOutputSilent = () => false;
        page.NativeOutputAvailable = () => true;
        page.FindApps = _ => [new AppAudio(101, false, "CI Game", null) { ExeKey = "game" },
            new AppAudio(102, false, "CI Music", null) { ExeKey = "music" }, new AppAudio(103, false, "CI Voice", null) { ExeKey = "voice" }];
        var settingsUris = new List<string>();
        foreach (var help in Descendants(page).OfType<MixerModeHelp>()) help.LaunchSettings = uri => settingsUris.Add(uri);
        form.Controls.Add(page);
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Set(app, "_ui", ui);
        long command = -1; int externalSaves = 0;
        void Audit()
        {
            var settings = MixerRoutes.Read(scenario.Config).Settings!;
            object Options(string? key, bool all = false) => MixerDestinationOptions.Build(scenario.Config, scenario.Scan,
                key == null ? settings.Default : settings.Apps.GetValueOrDefault(key), key != null,
                route => scenario.Coordinator.PreviewRoute(scenario.Config, key, route, all));
            var value = new
            {
                connected = scenario.Coordinator.IsConnected, selected = scenario.Coordinator.SelectedKey,
                statuses = scenario.Coordinator.Statuses, routing = MixerRoutes.Read(scenario.Config).Settings,
                saves = scenario.Saves, externalSaves, scenario.HubFactories,
                captures = scenario.Host?.Live, opens = scenario.Host?.Opens,
                samples = scenario.Checked, nonzero = scenario.Nonzero, errors = scenario.Errors,
                nativeDefaultId = MixerScenario.NativeId, captureDeviceId = scenario.Config.CaptureDeviceId,
                command, sendVolumes = scenario.Config.AppSendVolumes, leaseCount = scenario.Coordinator.Leases.Count,
                settingsUris, options = new { defaults = Options(null), all = Options(null, true), game = Options("game"), music = Options("music"), voice = Options("voice") },
                senders = scenario.Coordinator.Statuses.ToDictionary(s => s.Key, s =>
                    new { epoch = scenario.Controller(s.Key)?.ActiveSender?.T0, fifo = scenario.Controller(s.Key)?.Fifo.TargetFrames }),
            };
            string temp = auditFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, auditFile, true);
        }
        scenario.Coordinator.Changed += () => ui.Post(_ =>
        {
            if (form.IsDisposed) return;
            Set(app, "<Controller>k__BackingField", scenario.Coordinator.SelectedController ?? initial);
            page.UpdateState(); page.ShowSoundOptions(); Audit();
        }, null);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += async (_, _) =>
        {
            string path = auditFile + ".command";
            if (File.Exists(path))
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var json = JsonDocument.Parse(stream);
                long id = json.RootElement.GetProperty("id").GetInt64();
                if (id != command)
                {
                    command = id;
                    switch (json.RootElement.GetProperty("op").GetString())
                    {
                        case "lose-music": scenario.LoseMusic(); break;
                        case "reconnect-music": await scenario.Coordinator.ReconnectAsync("group:" + MixerScenario.MusicGroup, ConnectTrigger.Manual); break;
                        case "external-rule":
                            var changed = MixerRoutes.Read(scenario.Config).Settings!;
                            changed.Apps["late-inactive"] = MixerRoute.Local; MixerRoutes.Write(scenario.Config, changed);
                            app.SaveMixerConfig(); externalSaves++; break;
                        case "disconnect": await scenario.Coordinator.DisconnectAsync(); break;
                        case "quit": form.Close(); break;
                    }
                }
            }
            Audit();
        };
        form.Shown += async (_, _) => { page.PageShown(); timer.Start(); await scenario.Connect(); Audit(); };
        Application.Run(form);
        timer.Stop(); page.PageHidden(); scenario.DisposeAsync().AsTask().GetAwaiter().GetResult();
        initial.Dispose(); Audit(); Log.CloseFile();
    }

    private static IEnumerable<Control> Descendants(Control root)
    { foreach (Control control in root.Controls) { yield return control; foreach (var nested in Descendants(control)) yield return nested; } }
    private static void Set(object target, string field, object value) =>
        (target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(field)).SetValue(target, value);
}
