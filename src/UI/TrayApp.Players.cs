using HomePodCast.Players;

namespace HomePodCast.UI;

// 「影视场景自动调整播放器」: local players' audio delay follows the HomePod while the 影视 scene streams.
internal sealed partial class TrayApp
{
    private PlayerSync? _players;

    /// <summary>Called once from the constructor, after the main window exists.</summary>
    private void InitPlayers()
    {
        _players = new PlayerSync(new PlayerScanner(VlcPassword.From(() => Config)));
        _players.Changed += () => _ui.Post(_ => _form.ShowPlayers(), null);
        Controller.Changed += _players.Kick;
        _players.Start(PlayerInput);
    }

    /// <summary>Status of the local players while the 影视 scene streams (empty otherwise).</summary>
    public IReadOnlyList<PlayerStatus> PlayerStatuses => _players?.Statuses ?? [];

    /// <summary>How late the HomePod is heard now, as the local API reports it (0 when not streaming).</summary>
    public int VideoDelayMs => VideoDelayFor(PlaybackConfig, Controller.State, Controller.EffectiveLatencyMs,
        Controller.Capture?.ExtraLatencyMs ?? 0, Controller.Fifo.TargetMs, CurrentCalibrationContext);

    /// <summary>
    /// The sound lag the local API (browser extension) and the players get: the estimate plus the calibration of the path
    /// streaming now (<paramref name="context"/>, the same one 首页 uses), 0 when not streaming.
    /// </summary>
    internal static int VideoDelayFor(AppConfig cfg, StreamState state, int effectiveLatencyMs, int captureExtraMs,
        double fifoTargetMs, AvCalibrationContext? context) => state == StreamState.Streaming
        ? LatencyTuner.SoundLagMs(effectiveLatencyMs, cfg, captureExtraMs, fifoTargetMs, context)
        : 0;

    private PlayerSyncInput PlayerInput() => Mixer is { IsConnected: true } mixer &&
        MixerRoutes.Read(Config).Settings is { } routing && (mixer.UsesHub || routing.Apps.Count > 0)
        ? new PlayerSyncInput(Config.MoviePlayerSync, StreamState.Streaming, 0)
        {
            ForEndpoint = endpoint =>
            {
                var destination = mixer.DestinationForApp(endpoint.ProcessKey, endpoint.RoutingProcessId);
                var controller = mixer.ControllerForApp(endpoint.ProcessKey, endpoint.RoutingProcessId);
                if (endpoint.ProcessKey == null || destination?.Config is not { } config || controller == null) return null;
                config.SyncCalibrations = Config.SyncCalibrations;
                config.VideoDelayExtraMs = Config.VideoDelayExtraMs;
                var context = MixerCalibrationContext(config, controller, destination.Key);
                return PlayerInputFor(config, controller.State, VideoDelayFor(config, controller.State,
                    controller.EffectiveLatencyMs, controller.Capture?.ExtraLatencyMs ?? 0, controller.Fifo.TargetMs, context));
            },
        }
        : PlayerInputFor(Config, Controller.State, VideoDelayMs);

    private object ApiStatusForApp(string? app, uint? pid)
    {
        if (Mixer is not { IsConnected: true } mixer)
            return ApiStatus(Controller.State == StreamState.Streaming, Config.DeviceName, Controller.EffectiveLatencyMs, VideoDelayMs, HasCurrentCalibration);
        if (app == null && pid is { } process && Audio.ProcessTree.Snapshot().TryGetValue(process, out var caller) &&
            MixerRoutes.Read(Config).Settings?.For(caller.Key).Output.Kind == MixerOutputKind.Local) app = caller.Key;
        var destination = mixer.DestinationForApp(app, pid);
        if (destination == null)
            return ApiUndelayedStatus(null, false, mixer.Statuses.Select(s => new { s.Key, s.Name, s.State }));
        var controller = mixer.ControllerForApp(app, pid);
        if (destination.Config is not { } config || controller == null)
            return ApiUndelayedStatus(destination.Key, destination.IsLocal, null);
        config.SyncCalibrations = Config.SyncCalibrations;
        config.VideoDelayExtraMs = Config.VideoDelayExtraMs;
        var context = MixerCalibrationContext(config, controller, destination.Key);
        int delay = VideoDelayFor(config, controller.State, controller.EffectiveLatencyMs,
            controller.Capture?.ExtraLatencyMs ?? 0, controller.Fifo.TargetMs, context);
        return ApiStatus(controller.State == StreamState.Streaming, destination.Name, controller.EffectiveLatencyMs,
            delay, AvCalibrations.Get(Config, context) != null);
    }

    /// <summary>
    /// GET /v1/status for a caller whose sound gets no speaker delay: its destination is unknown (<paramref name="destination"/>
    /// null; <paramref name="destinations"/> lists them) or has no stream running, or the caller plays locally.
    /// reason lets the browser extension say so in the browser's language; message (the app's) is for older extensions.
    /// </summary>
    internal static object ApiUndelayedStatus(string? destination, bool local, object? destinations)
    {
        string reason = local ? "local-output" : "destination-unknown";
        string message = local ? L.T("此程序在本机播放，不使用音箱画面延迟。") : L.T("无法确定此程序的声音去处，同步暂不可用。");
        return destination == null
            ? new { app = "HomePodCast", streaming = false, destinationRequired = true, reason, message, destinations }
            : new { app = "HomePodCast", streaming = false, destination, local, destinationRequired = !local, reason, message };
    }

    /// <summary>What the players follow: on only in the 影视 scene with the setting on.</summary>
    internal static PlayerSyncInput PlayerInputFor(AppConfig cfg, StreamState state, int videoDelayMs) =>
        new(cfg.MoviePlayerSync && cfg.Scene == Scene.Movie, state, videoDelayMs);

    /// <summary>GET /v1/status of the local API, which the browser extension reads (the same lag the players get).</summary>
    internal static object ApiStatus(bool streaming, string? device, int latencyMs, int videoDelayMs, bool calibrated) => new
    {
        app = "HomePodCast",
        version = typeof(TrayApp).Assembly.GetName().Version?.ToString(3),
        streaming,
        device,
        latencyMs,
        videoDelayMs,
        videoDelaySource = calibrated ? "calibrated" : "estimate",
    };

    /// <summary>The scene or the setting changed: adjust or restore the players now instead of at the next poll.</summary>
    public void KickPlayers() => _players?.Kick();

    public void SetMoviePlayerSync(bool on)
    {
        Config.MoviePlayerSync = on;
        Config.Save();
        Log.Info($"movie player sync {(on ? "on" : "off")}");
        KickPlayers();
        _form.ShowPlayers();
    }

    /// <summary>A VLC password was typed in 设置 (it is kept encrypted only).</summary>
    public bool HasVlcPassword => !string.IsNullOrEmpty(Config.VlcPasswordProtected);

    /// <summary>
    /// Saves the VLC password typed in 设置, encrypted for this Windows user (null or empty: forget it), and
    /// tries VLC again at once. Throws when Windows cannot encrypt it; nothing is saved then.
    /// </summary>
    public void SetVlcPassword(string? password)
    {
        Config.VlcPasswordProtected = string.IsNullOrEmpty(password) ? null : VlcPassword.Protect(password);
        Config.Save();
        Log.Info(Config.VlcPasswordProtected == null ? "players: VLC password from Settings cleared" : "players: VLC password from Settings saved (encrypted)");
        KickPlayers();
    }

    /// <summary>Puts every player back to its own delay (app exit).</summary>
    private void DisposePlayers()
    {
        if (_players == null) return;
        Controller.Changed -= _players.Kick;
        _players.Shutdown(TimeSpan.FromSeconds(2));
    }
}
