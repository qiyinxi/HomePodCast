using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal sealed partial class MixerPage
{
    private readonly FluentComboBox _switchAll = new() { PreferredWidth = 290, AccessibleName = L.T("全部切到") };
    private readonly FluentButton _switchButton = new(L.T("切换"));
    private readonly TextBlock _destinationError = new("", TextStyle.Body, TextRole.Caution, wrap: true);
    private readonly StackPanel _destinationStates = Ui.Stack(8);
    private readonly Dictionary<FluentComboBox, List<MixerDestinationOption>> _destinationChoices = [];
    private string? _stateSignature;
    internal Func<string, string, bool> ConfirmDestinations { get; set; } = (text, title) =>
        MessageBox.Show(text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    private void InitializeDestinations(Control modes)
    {
        var title = Ui.Section(L.T("多去处混音（实验性）")); // experimental since 1.3
        var controls = Ui.Row(8, null, _switchAll, _switchButton); controls.Wrap = true;
        var allRow = new SettingRow(Glyph.Devices, L.T("全部切到"),
            L.T("把所有程序的声音都改送到这里，包括单独设置过去处的程序。之后仍可在下面逐个调整。不改变 Windows 默认输出设备。"), controls);
        var identities = new FluentButton(L.T("确认设备身份"), ButtonKind.Link);
        identities.Click += async (_, _) => await ConfirmPhysicalIdentities();
        var stateTitle = Ui.Section(L.T("各去处状态（实验性）")); // experimental since 1.3
        var cost = Ui.Note(L.T("更多去处和压缩编码会增加 CPU 与内存占用。不同去处互不保证同步。"));
        var restore = Ui.Note(L.F("仅保留一个远端去处，让所有程序跟随它，并关闭独立音量，即可回到「{0}」的低延迟路径。", L.T("全部推送")));
        Content.Controls.AddRange([title, allRow, _destinationError, identities, stateTitle, _destinationStates, cost, restore]);
        Content.Controls.SetChildIndex(title, 3); Content.Controls.SetChildIndex(_routeRow, 4); Content.Controls.SetChildIndex(allRow, 5);
        Content.Controls.SetChildIndex(_destinationError, 6); Content.Controls.SetChildIndex(identities, 7);
        Content.Controls.SetChildIndex(stateTitle, 8); Content.Controls.SetChildIndex(_destinationStates, 9);
        _switchButton.Click += async (_, _) => await SwitchAll();
    }

    private List<MixerDestinationOption> Options(string? key, MixerRoute? selected, bool all = false) =>
        MixerDestinationOptions.Build(_app.Config, _app.LastScan, selected, key != null,
            route => _app.Mixer?.PreviewRoute(_app.Config, key, route, all) ?? new(MixerEditResult.Applied));

    private void Fill(FluentComboBox combo, List<MixerDestinationOption> choices, MixerRoute? selected)
    {
        if (combo.DroppedDown) return;
        if (!_destinationChoices.TryGetValue(combo, out var old) || !old.SequenceEqual(choices))
        {
            combo.Items.Clear(); foreach (var choice in choices) combo.Items.Add(choice.Label);
            _destinationChoices[combo] = choices;
            combo.IsItemEnabled = index => index >= 0 && index < _destinationChoices[combo].Count && _destinationChoices[combo][index].Enabled;
            combo.ItemDisabledReason = index => index >= 0 && index < _destinationChoices[combo].Count ? _destinationChoices[combo][index].Reason : null;
        }
        combo.SelectedIndex = choices.FindIndex(o => MixerDestinationOptions.Same(o.Route, selected));
        combo.Enabled = AppRouting.Supported && MixerRoutes.Read(_app.Config).Editable;
    }

    private void SyncDestinationChoices()
    {
        var routing = MixerRoutes.Read(_app.Config);
        Fill(_routeDefault, Options(null, routing.Settings?.Default), routing.Settings?.Default);
        var chosen = Choice(_switchAll)?.Route ?? routing.Settings?.Default;
        Fill(_switchAll, Options(null, chosen, true), chosen);
        _switchButton.Enabled = routing.Editable && Choice(_switchAll) is { Enabled: true, Route: not null };
        if (!routing.Editable) _destinationError.Text = MixerDestinationOptions.Error(routing.Error);
        ShowMaster();
        SyncDestinationStates();
    }

    private MixerDestinationOption? Choice(FluentComboBox combo) => _destinationChoices.TryGetValue(combo, out var choices) &&
        combo.SelectedIndex >= 0 && combo.SelectedIndex < choices.Count ? choices[combo.SelectedIndex] : null;

    private void SyncAppChoice(string key, FluentComboBox combo)
    {
        var settings = MixerRoutes.Read(_app.Config).Settings;
        MixerRoute? selected = settings?.Apps.GetValueOrDefault(RouteRules.KeyFor(key));
        Fill(combo, Options(key, selected), selected);
    }

    private async Task ApplyDefaultChoice()
    {
        var settings = MixerRoutes.Read(_app.Config).Settings;
        if (settings == null || Choice(_routeDefault) is not { Route: { } route, Enabled: true }) return;
        await SaveRouting(settings with { Default = route });
    }

    private async Task ApplyAppChoice(string key, FluentComboBox combo)
    {
        var settings = MixerRoutes.Read(_app.Config).Settings;
        if (settings == null || Choice(combo) is not { Enabled: true } choice) return;
        var apps = new Dictionary<string, MixerRoute>(settings.Apps);
        if (choice.Route is { } route) apps[RouteRules.KeyFor(key)] = route; else apps.Remove(RouteRules.KeyFor(key));
        await SaveRouting(settings with { Apps = apps });
    }

    private bool ConfirmNewRouting(MixerRoutingSettings settings)
    {
        bool remote = new[] { settings.Default }.Concat(settings.Apps.Values).Any(r => r.Output.Kind != MixerOutputKind.Local);
        bool routed = remote && (MixerOutputCoordinator.NeedsHub(_app.Config, settings) || settings.Default.Output.Kind == MixerOutputKind.Local ||
            settings.Apps.Values.Any(r => r.Output.Kind == MixerOutputKind.Local || r.AlsoLocal != settings.Default.AlsoLocal) ||
            _app.Config.AppSendVolumes.Keys.Any(k => settings.For(k).Output.Kind != MixerOutputKind.Local));
        if (!routed || _app.Config.RoutingNoticeAccepted) return true;
        return ConfirmDestinations(L.F("按程序采集会让送往音箱的声音多约 {0} ms 延迟，游戏无法补偿。要继续吗？", RoutedCapture.RoutedExtraLatencyMs),
            L.T("按程序分流会增加延迟"));
    }

    private async Task SaveRouting(MixerRoutingSettings settings)
    {
        if (!ConfirmNewRouting(settings)) { SyncRoutes(); return; }
        bool accepted = _app.Config.RoutingNoticeAccepted;
        _app.Config.RoutingNoticeAccepted = true;
        var outcome = await _app.ApplyMixerSettingsAsync(settings);
        if (outcome.Result != MixerEditResult.Applied) _app.Config.RoutingNoticeAccepted = accepted;
        _destinationError.Text = outcome.Result == MixerEditResult.Applied ? "" : MixerDestinationOptions.Error(outcome, _app.Config);
        if (outcome.Result == MixerEditResult.Applied) _app.Config.RoutingNoticeAccepted = true;
        SyncRoutes();
    }

    private async Task SwitchAll()
    {
        if (_app.Mixer == null || Choice(_switchAll) is not { Enabled: true, Route: { } route }) return;
        _switchButton.Enabled = false;
        try
        {
            while (true)
            {
                var preview = _app.Mixer.PreviewSwitch(_app.Config);
                if (preview.Overrides > 0 && !ConfirmDestinations(L.F("{0} 个程序的单独去处设置会被覆盖。要继续吗？", preview.Overrides), L.T("全部切到"))) return;
                var settings = MixerRoutes.Read(_app.Config).Settings! with { Default = route, Apps = [] };
                if (!ConfirmNewRouting(settings)) return;
                bool accepted = _app.Config.RoutingNoticeAccepted;
                _app.Config.RoutingNoticeAccepted = true;
                var outcome = await _app.Mixer.SwitchAllAsync(_app.Config, route, preview, _app.SaveMixerConfig);
                if (outcome.Result != MixerEditResult.Applied) _app.Config.RoutingNoticeAccepted = accepted;
                if (outcome.Result == MixerEditResult.Stale) continue;
                _destinationError.Text = outcome.Result == MixerEditResult.Applied ? "" : MixerDestinationOptions.Error(outcome, _app.Config);
                if (outcome.Result == MixerEditResult.Applied) { _app.Config.RoutingNoticeAccepted = true; _app.Routing.Reload(); }
                return;
            }
        }
        finally { SyncRoutes(); }
    }

    private void SyncDestinationStates()
    {
        var statuses = _app.Mixer?.Statuses ?? [];
        var routing = MixerRoutes.Read(_app.Config).Settings;
        bool localWanted = routing != null && new[] { routing.Default }.Concat(routing.Apps.Values).Any(r => r.Output.Kind == MixerOutputKind.Local || r.AlsoLocal);
        bool localSilent = localWanted && IsLocalOutputSilent();
        bool localAvailable = !localWanted || NativeOutputAvailable();
        string signature = string.Join("|", statuses.Select(s => $"{s.Key}:{s.State}:{s.Status}:{s.RequestedLatencyMs}:{s.EffectiveLatencyMs}:{s.CaptureExtraMs}:" +
            string.Join(";", s.Members.Select(m => m.Name + ":" + m.Status)) + s.Conflict + s.Error)) + localWanted + localSilent + localAvailable;
        if (signature == _stateSignature) return;
        _stateSignature = signature;
        using var batch = LayoutPanel.BatchRelayout(_destinationStates);
        foreach (Control control in _destinationStates.Controls.Cast<Control>().ToArray()) control.Dispose();
        _destinationStates.Controls.Clear();
        foreach (var status in statuses)
        {
            var latency = new FluentButton(L.T("调整延迟")) { Enabled = !status.Pending };
            latency.Click += async (_, _) =>
            {
                var controller = _app.Mixer?.ControllerForKey(status.Key);
                Net.ReceiverLatencySelection? receiver = null;
                if (status.Members.Count == 0 && _app.Mixer?.DestinationForKey(status.Key)?.Config?.DeviceId is { } deviceId)
                    receiver = Net.ReceiverLatencySelection.Create(controller?.ReceiverLookup?.Invoke(deviceId), controller?.ReceiverSettings?.Invoke(deviceId), controller?.ActiveProtocol);
                using var dialog = new MixerDestinationDelayDialog(status, MixerRoutes.Read(_app.Config).Settings?.DestinationSettings.GetValueOrDefault(status.Key), receiver);
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK || MixerRoutes.Read(_app.Config).Settings is not { } settings) return;
                var outcome = await _app.ApplyMixerDelayAsync(status.Key, dialog.Settings);
                _destinationError.Text = outcome.Result == MixerEditResult.Applied ? "" : MixerDestinationOptions.Error(outcome, _app.Config);
                SyncRoutes();
            };
            var control = new FluentButton(L.T("控制此去处")) { Enabled = !status.Pending };
            control.Click += (_, _) => { _app.Mixer?.Select(status.Key); ShowMaster(); };
            var stop = new FluentButton(L.T("停止此去处"));
            stop.Click += async (_, _) => { if (_app.Mixer != null) await _app.Mixer.DisconnectAsync(status.Key); SyncRoutes(); };
            var connect = new FluentButton(L.T("连接")) { Enabled = !status.Pending && TrayApp.ShowsConnect(status.State) };
            connect.Click += async (_, _) => { await _app.ConnectMixerManually(status.Key, FindForm()); SyncRoutes(); };
            var actions = Ui.Row(8, null, control, latency, connect, stop); actions.Wrap = true;
            var text = status.Status + "\n" + L.F("请求 {0} ms · 实际 {1} ms", status.RequestedLatencyMs, status.EffectiveLatencyMs);
            if (status.Pending) text = status.Conflict != null ? MixerDestinationOptions.Error(new(MixerEditResult.Conflict, status.Conflict), _app.Config)
                : MixerDestinationOptions.Error(status.Error);
            if (status.CaptureExtraMs > 0) text += "\n" + L.F("按程序采集额外延迟约 {0} ms。", status.CaptureExtraMs);
            foreach (var member in status.Members) text += "\n" + L.F("{0}：{1}", member.Name, member.Status);
            _destinationStates.Controls.Add(new SettingRow(Glyph.Speaker, status.Name, text, actions));
        }
        if (localWanted)
            _destinationStates.Controls.Add(new SettingRow(Glyph.Speaker, L.T("本机"),
                L.T("本机播放由 Windows 决定；不创建播放副本，也不提供网络延迟设置。") +
                "\n" + (!localAvailable ? L.T("Windows 没有可用的本机输出。") : localSilent ? L.T("Windows 输出已静音：本机没有声音。") : L.T("本机输出可用。")), null));
        if (_destinationStates.Controls.Count == 0) _destinationStates.Controls.Add(Ui.Note(L.T("连接音箱后生效。")));
    }

    private async Task ConfirmPhysicalIdentities()
    {
        using var dialog = new MixerDeviceIdentityDialog(_app.Config);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        MixerEditOutcome outcome;
        if (_app.Mixer != null) outcome = await _app.Mixer.UpdateIdentitiesAsync(_app.Config, dialog.Identities, _app.SaveMixerConfig);
        else
        {
            var previous = _app.Config.MixerDeviceIdentities; _app.Config.MixerDeviceIdentities = dialog.Identities;
            try { _app.SaveMixerConfig(); outcome = new(MixerEditResult.Applied); }
            catch { _app.Config.MixerDeviceIdentities = previous; outcome = new(MixerEditResult.SaveFailed); }
        }
        _destinationError.Text = outcome.Result == MixerEditResult.Applied ? "" : MixerDestinationOptions.Error(outcome, _app.Config);
        SyncRoutes();
    }
}
