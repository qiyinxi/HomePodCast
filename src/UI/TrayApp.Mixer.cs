using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private StreamController? _legacyController;
    internal MixerOutputCoordinator? Mixer { get; private set; }
    internal AppConfig PlaybackConfig
    {
        get
        {
            var selected = Mixer?.SelectedConfig ?? Config;
            selected.SyncCalibrations = Config.SyncCalibrations;
            return selected;
        }
    }
    private readonly System.Collections.Concurrent.ConcurrentDictionary<StreamController, Equalizer> _mixerEqualizers = new();
    private MixIn? _legacyMicMix;
    private bool _mixerMuted;
    private AppConfig? _shownMixerConfig;

    private void InitMixer()
    {
        Mixer = new(new MixerDeviceLeaseTable(), CreateMixerSession, LegacyMixerCapture,
            () => new MixerProcessCaptureHost(new CombinedSources(MixSources, Fx.HomePodSource).Sources, () => Routing.Rules.SendVolumes, Routing), () => LastScan);
        _legacyMicMix = _legacyController!.Effects.Effects.OfType<MixIn>().FirstOrDefault();
        Mixer.Changed += () => _ui.Post(_ =>
        {
            if (_quitting) return;
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
        if (ReferenceEquals(Mixer?.SelectedController, _legacyController) ||
            MixerDestinationResolver.Resolve(Config, MixerRoute.Current, LastScan).Key == destination.Key)
            return new RoutedCapture(fifo, RtpSender.SampleRate, Routing, MixSources);
        var projection = destination.Config!;
        var routing = MixerRoutes.Read(Config).Settings!;
        AudioRoute Legacy(MixerRoute route) => route.Output.Kind == MixerOutputKind.Local ? AudioRoute.Local : route.AlsoLocal ? AudioRoute.Both : AudioRoute.HomePod;
        projection.RouteDefault = Legacy(routing.Default);
        projection.AppRoutes = routing.Apps.ToDictionary(p => p.Key, p => Legacy(p.Value));
        return new RoutedCapture(fifo, RtpSender.SampleRate, new AppRouting(projection, () => { }), MixSources);
    }

    private IMixerDestinationSession CreateMixerSession(ResolvedMixerDestination destination, Func<AudioFifo, ICaptureSource> capture)
    {
        var config = destination.Config!;
        bool current = destination.Key == MixerDestinationResolver.Resolve(Config, MixerRoute.Current, LastScan).Key;
        var controller = current ? _legacyController! : new StreamController(config.FifoTargetMs);
        controller.CaptureFactory = capture;
        controller.ArrivalToRenderMs = current ? Config.ArrivalToRenderMs : config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(config.DeviceId ?? ""))?.ArrivalToRenderMs;
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
        controller.ReceiverSettings = id => Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id));
        controller.CompatibilityLatency = id => Config.ReceiverSettings.GetValueOrDefault(RaopPassword.Key(id))?.CompatibilityLatencyMs ?? 2000;
        controller.ReceiverPassword = id => RaopPassword.Unprotect(id, Config.RaopPasswordsProtected.GetValueOrDefault(RaopPassword.Key(id)));
        controller.CapturedOutputId = () => CaptureEndpoint.Captured(Routing.CaptureDeviceId, AudioEndpoints.DefaultOutputId());
        if (current && _legacyMicMix != null)
        {
            controller.Effects.Remove(_legacyMicMix);
            if (Mixer?.UsesHub != true) controller.Effects.Insert(0, _legacyMicMix);
        }
        if (!current)
        {
            var eq = new Equalizer(44100); eq.Set(Config.Effects.OutputEq.Preset, Config.Effects.OutputEq.Gains);
            controller.Effects.Insert(0, eq); _mixerEqualizers[controller] = eq;
            if (Mixer?.UsesHub != true) controller.Effects.Insert(0, new MixIn(Fx.HomePodSource));
            controller.Effects.Add(_soundActivity);
            controller.Changed += () => _ui.Post(_ => { if (!_quitting) RememberMixerSession(destination.Key, config, controller); }, null);
            controller.HostResolved += host => config.Host = host;
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

    private void StopAllOutputs(string? reason = null)
    {
        if (Mixer != null) Mixer.DisconnectAsync(reason: reason).GetAwaiter().GetResult();
        if (reason == null) _legacyController?.Stop(); else _legacyController?.Stop(reason);
    }

    internal void SaveSelectedDestinationSettings()
    {
        if (Mixer?.SelectedConfig is not { } selected || Mixer.Statuses.FirstOrDefault(s => s.Key == Mixer.SelectedKey) is not { } status) return;
        if (ReferenceEquals(Controller, _legacyController))
        { Config.Scene = selected.Scene; Config.LatencyMs = selected.LatencyMs; Config.CustomLatencyMs = selected.CustomLatencyMs; Config.Save(); }
        else if (MixerRoutes.Read(Config).Settings is { } settings)
        {
            settings.DestinationSettings[status.Key] = new(selected.Scene, selected.LatencyMs, selected.FifoTargetMs, selected.Volume);
            MixerRoutes.Write(Config, settings); Config.Save();
        }
    }
    internal string? SelectedMixerKey => Mixer?.SelectedKey;

    private void SaveSelectedVolume(double percent)
    {
        if (Mixer?.SelectedConfig is { } selected && !ReferenceEquals(Controller, _legacyController) &&
            SelectedMixerKey is { } key && MixerRoutes.Read(Config).Settings is { } settings)
        {
            selected.Volume = percent;
            var saved = settings.DestinationSettings.GetValueOrDefault(key) ?? new(selected.Scene, selected.LatencyMs, selected.FifoTargetMs);
            settings.DestinationSettings[key] = saved with { Volume = percent };
            MixerRoutes.Write(Config, settings); Config.Save();
        }
        else { Config.Volume = percent; Config.Save(); }
    }

    internal async Task<MixerEditOutcome> ApplyMixerSettingsAsync(MixerRoutingSettings settings)
    {
        if (Mixer == null) { MixerRoutes.Write(Config, settings); Config.Save(); Routing.Reload(); return new(MixerEditResult.Applied); }
        var result = await Mixer.EditAsync(Config, settings, Config.Save);
        if (result.Result == MixerEditResult.Applied) { Routing.Reload(); RaiseStateChanged(); }
        return result;
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
            controller.SetVolumeCap(Config.VolumeCapPercent);
            if (_mixerEqualizers.TryGetValue(controller, out var eq))
            {
                if (Config.NightMode) eq.SetPreset(EqPreset.ReduceBass);
                else eq.Set(Config.Effects.OutputEq.Preset, Config.Effects.OutputEq.Gains);
            }
        }
    }
}
