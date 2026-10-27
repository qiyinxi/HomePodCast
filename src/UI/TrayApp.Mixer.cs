using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private StreamController? _legacyController;
    private string? _legacyDestinationKey;
    internal MixerOutputCoordinator? Mixer { get; private set; }
    internal AppConfig PlaybackConfig
    {
        get
        {
            var selected = Mixer?.SelectedConfig ?? Config;
            selected.SyncCalibrations = Config.SyncCalibrations;
            selected.ReceiverSettings = Config.ReceiverSettings;
            selected.VideoDelayExtraMs = Config.VideoDelayExtraMs;
            selected.VolumeCapPercent = Config.VolumeCapPercent;
            selected.NightMode = Config.NightMode;
            selected.CaptureDeviceId = Routing.CaptureDeviceId;
            return selected;
        }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<StreamController, Equalizer> _mixerEqualizers = new();
    private MixIn? _legacyMicMix;
    private bool _mixerMuted;
    private AppConfig? _shownMixerConfig;

    private void InitMixer()
    {
        _legacyDestinationKey = MixerDestinationResolver.Resolve(Config, MixerRoute.Current, LastScan).Key;
        Mixer = new(new MixerDeviceLeaseTable(), CreateMixerSession, LegacyMixerCapture,
            () => new MixerProcessCaptureHost(new CombinedSources(MixSources, Fx.HomePodSource).Sources, () => Routing.Rules.SendVolumes, Routing), () => LastScan,
            () => _knownDefaultOutputId);
        _legacyMicMix = _legacyController!.Effects.Effects.OfType<MixIn>().FirstOrDefault();
        Mixer.Changed += () => _ui.Post(_ =>
        {
            if (_quitting) return;
            _wantConnected = Mixer.IsConnected;
            var previous = Controller;
            if (_cinemaSession?.Holding != true) Controller = Mixer.SelectedController ?? _legacyController!;
            var projection = Mixer.SelectedConfig;
            if (!ReferenceEquals(previous, Controller) || !ReferenceEquals(projection, _shownMixerConfig))
            { _shownMixerConfig = projection; _form.ReloadConfiguration(); }
            OnControllerChanged(); KickPlayers(); UpdatePcMute(); RaiseStateChanged();
        }, null);
        Routing.Changed += () =>
        {
            if (Mixer.IsConnected) PostConnection(() => Mixer.ConnectAsync(Config, ConnectTrigger.Automatic).GetAwaiter().GetResult());
        };
    }

    private ICaptureSource LegacyMixerCapture(ResolvedMixerDestination destination, AudioFifo fifo)
    {
        // A bypass has one remote bus. Its capture policy is still the live global routing policy, even when
        // that bus is a named speaker: copying it would freeze later Local/Both and independent-volume edits.
        return new RoutedCapture(fifo, RtpSender.SampleRate, Routing, MixSources);
    }

    internal Action? SaveMixerConfiguration { get; set; }
    internal void SaveMixerConfig() => (SaveMixerConfiguration ?? Config.Save)();

    private IMixerDestinationSession CreateMixerSession(ResolvedMixerDestination destination, Func<AudioFifo, ICaptureSource> capture)
    {
        var config = destination.Config!;
        bool current = destination.Key == MixerDestinationResolver.Resolve(Config, MixerRoute.Current, LastScan).Key;
        bool useLegacy = destination.Key == _legacyDestinationKey &&
            (Mixer?.KeyForController(_legacyController!) is not { } owner || owner == destination.Key);
        var controller = useLegacy ? _legacyController! : new StreamController(config.FifoTargetMs);
        controller.CaptureFactory = capture;
        controller.ArrivalToRenderMs = current && useLegacy ? Config.ArrivalToRenderMs : config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(config.DeviceId ?? ""))?.ArrivalToRenderMs;
        controller.SetVolumeCap(Config.VolumeCapPercent); controller.NightMode.Enabled = Config.NightMode;
        controller.SetMuted(_mixerMuted);
        controller.Pairings = Pairings;
        controller.BusyReader = new InfoBusyReader(SpeakerInfo, () => LastScan);
        // A route edit or automatic retry is never permission to take a home-theater speaker over.
        controller.HomeTheaterMode = Config.HomeTheaterTakeover;
        controller.ManualGroupReceiverDevice = id => Config.ManualDevices.Select(d => d.ToDevice()).FirstOrDefault(d => d != null && RaopPassword.Key(d.DeviceId) == RaopPassword.Key(id));
        controller.ReceiverLookup = id => LastScan.FirstOrDefault(d => RaopPassword.Key(d.DeviceId) == RaopPassword.Key(id))
            ?? Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.Device(id)
            ?? controller.ManualGroupReceiverDevice(id);
        bool individualDelay = GroupPlan.FromConfig(config) == null &&
            MixerRoutes.Read(Config).Settings?.DestinationSettings.ContainsKey(destination.Key) == true;
        controller.ReceiverSettings = id =>
        {
            var preferences = Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id));
            if (!individualDelay) return preferences;
            var device = controller.ReceiverLookup?.Invoke(id);
            if (device == null) return preferences;
            preferences ??= ReceiverPreferences.Of(device);
            return preferences with { RealtimeLatencyMs = config.LatencyMs,
                CompatibilityLatencyMs = ReceiverLatencies.CompatibilityChoices.FirstOrDefault(ms => ms >= config.LatencyMs, 2000) };
        };
        controller.CompatibilityLatency = id => controller.ReceiverSettings(id)?.CompatibilityLatencyMs ?? 2000;
        controller.ReceiverPassword = id => RaopPassword.Unprotect(id, Config.RaopPasswordsProtected.GetValueOrDefault(RaopPassword.Key(id)));
        controller.CapturedOutputId = () => CaptureEndpoint.Captured(Routing.CaptureDeviceId, _knownDefaultOutputId);
        if (useLegacy && _legacyMicMix != null)
        {
            controller.Effects.Remove(_legacyMicMix);
            if (Mixer?.UsesHub != true) controller.Effects.Insert(0, _legacyMicMix);
        }
        if (!useLegacy)
        {
            var eq = new Equalizer(44100); ConfigureOutputEq(eq);
            controller.Effects.Insert(0, eq); _mixerEqualizers[controller] = eq;
            if (Mixer?.UsesHub != true) controller.Effects.Insert(0, new MixIn(Fx.HomePodSource));
            controller.Effects.Add(_soundActivity);
            controller.Changed += () => _ui.Post(_ => { if (!_quitting) RememberMixerSession(destination.Key, config, controller); }, null);
            controller.HostResolved += host =>
            {
                config.Host = host;
                _ui.Post(_ =>
                {
                    if (!_quitting && config.DeviceId != null && GroupPlan.Key(Config.DeviceId ?? "") == GroupPlan.Key(config.DeviceId))
                    { Config.Host = host; Config.Save(); }
                }, null);
            };
            controller.ArrivalToRenderChanged += ms => config.ArrivalToRenderMs = ms;
            controller.ReceiverProtocolConnected += (id, protocol, processing) => _ui.Post(_ =>
            {
                if (_quitting || controller.ReceiverLookup?.Invoke(id) is not { } device) return;
                string key = RaopPassword.Key(id);
                if (!Config.ReceiverSettings.TryGetValue(key, out var settings)) Config.ReceiverSettings[key] = settings = ReceiverPreferences.Of(device);
                settings.LastSuccessfulProtocol = protocol; if (processing != null) settings.ArrivalToRenderMs = processing;
                Config.Save();
            }, null);
            controller.PairingRequired += request => _ui.Post(_ => ShowMixerPairing(destination.Key, controller, request), null);
            controller.RaopPasswordRequired += request => _ui.Post(_ => ShowMixerPassword(destination.Key, controller, request), null);
            controller.RaopTakeoverRequired += request => _ui.Post(async _ => await HandleMixerTakeoverAsync(destination.Key, controller, request), null);
            InitializeGroupInteractions(controller);
        }
        bool FreshEnough()
        {
            if (GroupPlan.FromConfig(config) != null || config.DeviceId == null || config.Host != null) return true;
            var device = controller.ReceiverLookup?.Invoke(config.DeviceId);
            return device == null || device.Kind is ReceiverKind.HomePod or ReceiverKind.AppleTv ||
                LastScan.Any(d => GroupPlan.Key(d.DeviceId) == GroupPlan.Key(config.DeviceId)) || controller.ManualGroupReceiverDevice?.Invoke(config.DeviceId) != null;
        }
        return new OwnedSession(new MixerStreamSession(destination, controller, FreshEnough), () => _mixerEqualizers.TryRemove(controller, out _));
    }

    private void RememberMixerSession(string key, AppConfig projection, StreamController controller)
    {
        // Per-device measurements live with that receiver, never in the selected HomePod's global slot.
        if (projection.DeviceId is { } id && projection.ArrivalToRenderMs is { } ms &&
            Config.ReceiverSettings.TryGetValue(RaopPassword.Key(id), out var preferences) && preferences.ArrivalToRenderMs != ms)
        { preferences.ArrivalToRenderMs = ms; Config.Save(); }
    }

    private void ShowMixerPairing(string key, StreamController controller, PairingRequest request)
    {
        if (_quitting || _pairingOpen || !PairingPolicy.OffersPinPairing(request.Trigger) || Mixer?.Statuses.Any(s => s.Key == key) != true) return;
        _pairingOpen = true;
        try
        {
            bool paired = PairingDialog.Run(_form.Visible ? _form : null, request.Speaker, request.Reason,
                ct => PinPairingSession.StartAsync(request.Address, request.Port, ct, pairingOverRtsp: request.ThirdParty),
                pairing => Pairings.Save(request.DeviceId, request.Speaker, pairing), request.ThirdParty);
            if (paired && !_quitting) PostConnection(() => Mixer!.ReconnectAsync(key, ConnectTrigger.Manual).GetAwaiter().GetResult());
        }
        finally { _pairingOpen = false; }
    }

    private void ShowMixerPassword(string key, StreamController controller, RaopInteraction request)
    {
        if (_quitting || _raopDialog || !PairingPolicy.OffersPinPairing(request.Trigger) || Mixer?.Statuses.Any(s => s.Key == key) != true) return;
        _raopDialog = true;
        try
        {
            using var dialog = new RaopPasswordDialog(request.Device.Name);
            if (dialog.ShowDialog(_form.Visible ? _form : null) != DialogResult.OK || _quitting) return;
            Config.RaopPasswordsProtected[RaopPassword.Key(request.Device.DeviceId)] = RaopPassword.Protect(request.Device.DeviceId, dialog.Password);
            Config.Save(); PostConnection(() => Mixer!.ReconnectAsync(key, ConnectTrigger.Manual).GetAwaiter().GetResult());
        }
        finally { _raopDialog = false; }
    }

    internal async Task HandleMixerTakeoverAsync(string key, StreamController controller, RaopInteraction request,
        Func<string, bool>? confirm = null)
    {
        bool Current() => !_quitting && controller.State == StreamState.Idle && Mixer?.IsConnected == true && ReferenceEquals(Mixer.ControllerForKey(key), controller) &&
            Mixer.DestinationForKey(key)?.Config?.DeviceId is { } id && RaopPassword.Key(id) == RaopPassword.Key(request.Device.DeviceId);
        if (!Current() || _raopDialog || request.GroupGeneration != null || request.Trigger != ConnectTrigger.Manual) return;
        _raopDialog = true;
        try
        {
            bool accepted = confirm?.Invoke(request.Device.Name) ?? TakeoverDialog.Confirm(_form.Visible ? _form : null, request.Device.Name);
            if (accepted && Current()) await Mixer!.ReconnectAsync(key, ConnectTrigger.TakeOver);
        }
        catch (Exception ex) { Log.Warn($"mixer takeover {key}: {ex.Message}"); }
        finally { _raopDialog = false; }
    }

    private void StopAllOutputs(string? reason = null)
    {
        if (Mixer != null) Mixer.DisconnectAsync(reason: reason).GetAwaiter().GetResult();
        if (reason == null) _legacyController?.Stop(); else _legacyController?.Stop(reason);
    }

    internal void SaveSelectedDestinationSettings()
    {
        if (Mixer?.SelectedConfig is not { } selected || Mixer.Statuses.FirstOrDefault(s => s.Key == Mixer.SelectedKey) is not { } status) return;
        if (UsesCompatibilitySettings(status.Key))
        { Config.Scene = selected.Scene; Config.LatencyMs = selected.LatencyMs; Config.CustomLatencyMs = selected.CustomLatencyMs; Config.Save(); }
        else if (MixerRoutes.Read(Config).Settings is { } settings)
        {
            settings.DestinationSettings[status.Key] = new(selected.Scene, selected.LatencyMs, selected.FifoTargetMs, selected.Volume);
            MixerRoutes.Write(Config, settings); Config.Save();
        }
    }
    internal string? SelectedMixerKey => Mixer?.SelectedKey;
    internal string ConnectionActionText => ShowsConnect(Controller.State) ? L.T("连接") :
        Mixer?.HasMultipleDestinations == true ? L.T("断开全部") : L.T("断开");
    internal string? SelectedOutputName => Mixer?.Statuses.FirstOrDefault(s => s.Key == SelectedMixerKey)?.Name ??
        (NativeMixerView ? L.T("本机") : null);
    internal bool NativeMixerView => Mixer?.IsConnected == true && Mixer.SelectedConfig == null &&
        MixerRoutes.Read(Config).Settings?.Default.Output.Kind == MixerOutputKind.Local;
    internal bool NativeMixerOnly => NativeMixerView && MixerRoutes.Read(Config).Settings is { } routes &&
        routes.Apps.Values.All(route => route.Output.Kind == MixerOutputKind.Local);

    private void SaveSelectedVolume(double percent)
    {
        if (Mixer?.SelectedConfig is { } selected &&
            SelectedMixerKey is { } key && MixerRoutes.Read(Config).Settings is { } settings)
        {
            if (UsesCompatibilitySettings(key)) { Config.Volume = percent; Config.Save(); return; }
            selected.Volume = percent;
            var saved = settings.DestinationSettings.GetValueOrDefault(key) ?? new(selected.Scene, selected.LatencyMs, selected.FifoTargetMs);
            settings.DestinationSettings[key] = saved with { Volume = percent };
            MixerRoutes.Write(Config, settings); Config.Save();
        }
        else { Config.Volume = percent; Config.Save(); }
    }

    private bool UsesCompatibilitySettings(string key)
    {
        var settings = MixerRoutes.Read(Config).Settings;
        if (settings == null) return false;
        string current = MixerDestinationResolver.Resolve(Config, MixerRoute.Current, LastScan).Key;
        string defaultKey = MixerDestinationResolver.Resolve(Config, settings.Default, LastScan).Key;
        return key == current && (settings.Default.Output.Kind == MixerOutputKind.Current || defaultKey != key &&
            settings.Apps.Values.Any(r => r.Output.Kind == MixerOutputKind.Current));
    }

    internal async Task<MixerEditOutcome> ApplyMixerSettingsAsync(MixerRoutingSettings settings)
    {
        if (Mixer == null) { MixerRoutes.Write(Config, settings); SaveMixerConfig(); Routing.Reload(); return new(MixerEditResult.Applied); }
        var result = await Mixer.EditAsync(Config, settings, SaveMixerConfig);
        if (result.Result == MixerEditResult.Applied) { Routing.Reload(); RaiseStateChanged(); }
        return result;
    }

    internal async Task<MixerEditOutcome> ApplyMixerDelayAsync(string key, MixerDestinationSettings delay)
    {
        var settings = MixerRoutes.Read(Config).Settings;
        if (settings == null) return new(MixerEditResult.InvalidConfig);
        bool compatibility = UsesCompatibilitySettings(key);
        if (!compatibility) { settings.DestinationSettings[key] = delay; return await ApplyMixerSettingsAsync(settings); }
        if (Mixer?.ControllerForKey(key) is { } receiverController && PlaybackConfig.DeviceId is { } receiverId &&
            ReceiverLatencySelection.Create(receiverController.ReceiverLookup?.Invoke(receiverId), receiverController.ReceiverSettings?.Invoke(receiverId)) is { } selection)
        {
            string receiverKey = RaopPassword.Key(receiverId);
            var old = Config.ReceiverSettings.GetValueOrDefault(receiverKey);
            var next = old == null ? ReceiverPreferences.Of(selection.Device) : old with { };
            selection.Save(next, delay.LatencyMs); Config.ReceiverSettings[receiverKey] = next;
            try { SaveMixerConfig(); }
            catch { if (old == null) Config.ReceiverSettings.Remove(receiverKey); else Config.ReceiverSettings[receiverKey] = old; return new(MixerEditResult.SaveFailed); }
            if (Mixer.IsConnected) await Mixer.ReconnectAsync(key, ConnectTrigger.Reconnect);
            return new(MixerEditResult.Applied);
        }
        var projection = System.Text.Json.JsonSerializer.Deserialize<AppConfig>(System.Text.Json.JsonSerializer.Serialize(Config))!;
        projection.Scene = delay.Scene; projection.LatencyMs = delay.LatencyMs;
        if (Mixer?.ValidateConfiguration(projection) is { Result: not MixerEditResult.Applied } rejected) return rejected;
        var oldScene = Config.Scene; int oldLatency = Config.LatencyMs;
        Config.Scene = delay.Scene; Config.LatencyMs = delay.LatencyMs;
        try { SaveMixerConfig(); }
        catch { Config.Scene = oldScene; Config.LatencyMs = oldLatency; return new(MixerEditResult.SaveFailed); }
        return Mixer?.IsConnected == true ? await Mixer.ConnectAsync(Config, ConnectTrigger.Reconnect) : new(MixerEditResult.Applied);
    }

    private sealed class CombinedSources
    {
        public MixSources Sources { get; } = new();
        public CombinedSources(MixSources sources, AudioTap mic) => Sources.Add(new Shared(sources, mic));
        private sealed class Shared(MixSources sources, AudioTap mic) : IMixSource
        {
            private float[] _scratch = new float[8820];
            public void Read(Span<float> dest)
            { mic.Read(dest); StreamMixer.AddSources(dest, sources.Snapshot, ref _scratch); }
        }
    }

    private sealed class OwnedSession(IMixerDestinationSession inner, Action dispose) : IMixerDestinationSession
    {
        public bool WaitingForDiscovery => inner.WaitingForDiscovery;
        public StreamController? Controller => inner.Controller;
        public StreamState State => inner.State;
        public string Status => inner.Status;
        public int EffectiveLatencyMs => inner.EffectiveLatencyMs;
        public event Action? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
        public void Start(ConnectTrigger trigger) => inner.Start(trigger);
        public Task StopAsync(string? reason = null) => inner.StopAsync(reason);
        public void Dispose() { inner.Dispose(); dispose(); }
    }

    private void ApplyMixerEffects()
    {
        if (Mixer == null) return;
        foreach (var controller in Mixer.Controllers)
        {
            controller.NightMode.Enabled = Config.NightMode;
            if (controller.VolumeCapPercent != Config.VolumeCapPercent) controller.SetVolumeCap(Config.VolumeCapPercent);
            if (_mixerEqualizers.TryGetValue(controller, out var eq)) ConfigureOutputEq(eq);
        }
    }

    /// <summary>
    /// A mixer destination's output EQ: the saved one, or 「减弱低音」 while night mode is on (MicEffects.OutputPresetOverride
    /// does the same for the startup speaker). Also when the destination is created: 2026-10-27, a speaker connected while
    /// night mode was on played the saved EQ (e.g. 低音增强) with the night compressor until some setting changed.
    /// </summary>
    private void ConfigureOutputEq(Equalizer eq)
    {
        if (Config.NightMode) eq.SetPreset(EqPreset.ReduceBass);
        else eq.Set(Config.Effects.OutputEq);
    }
}
