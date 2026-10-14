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
    public int VideoDelayMs => VideoDelayFor(Config, Controller.State, Controller.EffectiveLatencyMs,
        Controller.Capture?.ExtraLatencyMs ?? 0, Controller.Fifo.TargetMs, CurrentCalibrationContext, Stems?.AddedLatencyMs ?? 0);

    /// <summary>
    /// The sound lag the local API (browser extension) and the players get: the estimate plus the calibration of the path
    /// streaming now (<paramref name="context"/>, the same one 首页 uses), plus the wait of 音乐分轨 while it is on
    /// (<paramref name="stemsMs"/>, which the sync test refuses to calibrate), 0 when not streaming.
    /// </summary>
    internal static int VideoDelayFor(AppConfig cfg, StreamState state, int effectiveLatencyMs, int captureExtraMs,
        double fifoTargetMs, AvCalibrationContext? context, int stemsMs = 0) => state == StreamState.Streaming
        ? LatencyTuner.SoundLagMs(effectiveLatencyMs, cfg, captureExtraMs, fifoTargetMs, context) + stemsMs
        : 0;

    private PlayerSyncInput PlayerInput() => PlayerInputFor(Config, Controller.State, VideoDelayMs);

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
