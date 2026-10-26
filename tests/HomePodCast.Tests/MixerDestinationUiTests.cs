using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.UI;
using HomePodCast.UI.Controls;
using HomePodCast.UI.Pages;

namespace HomePodCast.Tests;

public sealed class MixerDestinationUiTests
{
    [Fact]
    public Task Shared_pickers_keep_inactive_destinations_and_default_changes_preserve_overrides() => OnUi(() =>
    {
        using var fixture = new Fixture();
        var defaults = fixture.Options("_routeDefault");
        Assert.Contains(defaults, o => o.Route?.Output.Kind == MixerOutputKind.Local);
        Assert.Contains(defaults, o => o.Route?.Output.Kind == MixerOutputKind.Group);
        Assert.Contains(defaults, o => o.Route?.AlsoLocal == true);
        var before = MixerRoutes.Read(fixture.Config).Settings!.Apps.ToDictionary();
        fixture.Pick("_routeDefault", MixerRoute.Local);
        Invoke(fixture.Page, "ApplyDefaultChoice");
        Assert.Equal(before, MixerRoutes.Read(fixture.Config).Settings!.Apps);
        Assert.Equal(MixerRoute.Local, MixerRoutes.Read(fixture.Config).Settings!.Default);
        Assert.Equal(1, fixture.Saves);
        Assert.False(TestApp.Field<FluentSlider>(fixture.Page, "_master").Enabled);
        Assert.False(fixture.App.NativeMixerOnly); // Inactive remote rules still keep the connection intent available.
    });

    [Fact]
    public Task Replace_all_counts_inactive_rules_cancel_is_empty_and_confirmation_is_one_save() => OnUi(() =>
    {
        using var fixture = new Fixture(); fixture.Pick("_switchAll", MixerRoute.Local);
        string before = JsonSerializer.Serialize(fixture.Config);
        string? message = null;
        fixture.Page.ConfirmDestinations = (text, _) => { message = text; return false; };
        Invoke(fixture.Page, "SwitchAll");
        Assert.Contains("3", message);
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Config)); Assert.Equal(0, fixture.Saves);
        fixture.Page.ConfirmDestinations = (_, _) => true;
        Invoke(fixture.Page, "SwitchAll");
        Assert.Equal(1, fixture.Saves); Assert.Empty(MixerRoutes.Read(fixture.Config).Settings!.Apps);
        Assert.Equal(MixerRoute.Local, MixerRoutes.Read(fixture.Config).Settings!.Default);
        Assert.Equal(32, fixture.Config.AppSendVolumes["game"]);
        Assert.Equal("fake-capture", fixture.Config.CaptureDeviceId);
        Assert.True(fixture.App.NativeMixerOnly);
        Assert.Empty(fixture.Coordinator.Statuses); Assert.Equal(0, fixture.Coordinator.Leases.Count);
    });

    [Fact]
    public Task Replace_all_reconfirms_a_changed_count_and_retains_state_if_save_fails() => OnUi(() =>
    {
        using var fixture = new Fixture(); fixture.Pick("_switchAll", MixerRoute.Local);
        var messages = new List<string>();
        fixture.Page.ConfirmDestinations = (text, _) =>
        {
            messages.Add(text);
            if (messages.Count == 1)
            {
                var changed = MixerRoutes.Read(fixture.Config).Settings!; changed.Apps["later"] = MixerRoute.Local;
                MixerRoutes.Write(fixture.Config, changed);
            }
            return true;
        };
        Invoke(fixture.Page, "SwitchAll");
        Assert.Equal(2, messages.Count); Assert.Contains("3", messages[0]); Assert.Contains("4", messages[1]); Assert.Equal(1, fixture.Saves);
        var rules = MixerRoutes.Read(fixture.Config).Settings!; rules.Apps["restored"] = MixerRoute.Current; MixerRoutes.Write(fixture.Config, rules);
        Invoke(fixture.Page, "SyncRoutes"); fixture.Pick("_switchAll", MixerRoute.Local);
        string before = JsonSerializer.Serialize(fixture.Config); fixture.FailSave = true;
        Invoke(fixture.Page, "SwitchAll");
        Assert.Equal(before, JsonSerializer.Serialize(fixture.Config));
        Assert.Contains("保存失败", TestApp.Field<TextBlock>(fixture.Page, "_destinationError").Text);
    });

    [Fact]
    public Task Disabled_conflicts_are_blocked_by_menu_keyboard_and_accessibility() => OnUi(() =>
    {
        using var fixture = new Fixture();
        OutputGroups.Save(fixture.Config, "overlap", OutputGroups.Capture(fixture.Config), "overlap");
        Invoke(fixture.Page, "SyncRoutes");
        var combo = TestApp.Field<FluentComboBox>(fixture.Page, "_routeDefault");
        var choices = fixture.Options("_routeDefault");
        int blocked = choices.FindIndex(o => o.Route?.Output.Kind == MixerOutputKind.Group && o.Route.Output.Id == "overlap");
        // Default-only replacement can stop the old default. For an app the persistent default would still overlap.
        var appChoices = MixerDestinationOptions.Build(fixture.Config, [], null, true,
            route => fixture.Coordinator.PreviewRoute(fixture.Config, "new-app", route));
        var conflict = appChoices.Single(o => o.Route?.Output.Kind == MixerOutputKind.Group && o.Route.Output.Id == "overlap" && !o.Route.AlsoLocal);
        Assert.False(conflict.Enabled);
        combo.IsItemEnabled = index => index != blocked;
        int selected = combo.SelectedIndex;
        combo.AccessibilityObject.Value = combo.Items[blocked];
        Assert.Equal(selected, combo.SelectedIndex);
        combo.SelectedIndex = blocked - 1; combo.HandleKey(Keys.Down);
        Assert.NotEqual(blocked, combo.SelectedIndex);
        using var menu = combo.BuildMenu(); Assert.False(menu.Items[blocked].Enabled);
    });

    [Fact]
    public Task Unknown_routing_is_preserved_and_all_route_editors_are_disabled() => OnUi(() =>
    {
        using var fixture = new Fixture();
        fixture.Config.MixerRouting = JsonDocument.Parse("{\"Version\":999,\"future\":true}").RootElement.Clone();
        Invoke(fixture.Page, "SyncRoutes");
        Assert.False(TestApp.Field<FluentComboBox>(fixture.Page, "_routeDefault").Enabled);
        Assert.False(TestApp.Field<FluentComboBox>(fixture.Page, "_switchAll").Enabled);
        Assert.False(TestApp.Field<FluentButton>(fixture.Page, "_switchButton").Enabled);
        Assert.Equal("{\"Version\":999,\"future\":true}", fixture.Config.MixerRouting.Value.GetRawText());
        Assert.Equal(0, fixture.Saves);
    });

    private sealed class Fixture : IDisposable
    {
        internal readonly AppConfig Config = MixerCoordinatorTests.Config();
        internal readonly MixerOutputCoordinator Coordinator;
        internal readonly TrayApp App;
        internal readonly MixerPage Page;
        internal int Saves; internal bool FailSave;
        internal Fixture()
        {
            var settings = MixerCoordinatorTests.Routes(Config);
            settings.Apps["voice"] = MixerRoute.Local; settings.Apps["inactive"] = settings.Apps["music"];
            MixerRoutes.Write(Config, settings); Config.RoutingNoticeAccepted = true;
            Config.AppSendVolumes["game"] = 32; Config.CaptureDeviceId = "fake-capture";
            var harness = new MixerCoordinatorTests.Harness(); Coordinator = harness.Coordinator;
            Coordinator.ConnectAsync(Config, ConnectTrigger.Manual).GetAwaiter().GetResult();
            App = TestApp.Create(Config);
            typeof(TrayApp).GetField("<Mixer>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(App, Coordinator);
            App.SaveMixerConfiguration = () => { if (FailSave) throw new IOException("fake save failed"); Saves++; };
            Page = new MixerPage(App) { FindApps = _ => [], FindCaptureOutputs = () => [new("fake-capture", "fake capture", 0)],
                IsLocalOutputSilent = () => false, NativeOutputAvailable = () => true };
            Page.PageShown();
        }
        internal List<MixerDestinationOption> Options(string field)
        {
            var combo = TestApp.Field<FluentComboBox>(Page, field);
            return TestApp.Field<Dictionary<FluentComboBox, List<MixerDestinationOption>>>(Page, "_destinationChoices")[combo];
        }
        internal void Pick(string field, MixerRoute route)
        {
            var combo = TestApp.Field<FluentComboBox>(Page, field);
            combo.SelectedIndex = Options(field).FindIndex(o => MixerDestinationOptions.Same(o.Route, route));
        }
        public void Dispose() { Page.PageHidden(); Page.Dispose(); Coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult(); App.Controller.Dispose(); }
    }
    private static void Invoke(object target, string method)
    {
        var result = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
        if (result is Task task) task.GetAwaiter().GetResult();
    }
    private static async Task OnUi(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); done.SetResult(); } catch (Exception ex) { done.SetException(ex); } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task.WaitAsync(TimeSpan.FromSeconds(20)); thread.Join();
    }
}
