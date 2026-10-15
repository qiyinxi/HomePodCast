using HomePodCast.Audio;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    public void ShowProfiles(IWin32Window owner)
    {
        _form.ShowPage(AppPage.Profiles);
    }

    public void SaveUsageProfile(string? id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > UsageProfiles.MaxNameLength) throw new ArgumentException(nameof(name));
        _form.Flush();
        SavePendingVolume();
        // Capture the displayed delay after settling pending scene/slider edits.
        _form.FlushLatency();
        var profile = Config.UsageProfiles.FirstOrDefault(p => p.Id == id);
        if (profile == null)
        {
            if (Config.UsageProfiles.Count >= UsageProfiles.MaxProfiles) throw new InvalidOperationException();
            profile = new UsageProfile();
            Config.UsageProfiles.Add(profile);
        }
        profile.Name = name;
        profile.Settings = UsageProfiles.Capture(Config);
        Config.Save();
        _form.ShowProfiles();
    }

    public void DeleteUsageProfile(string id)
    {
        Config.UsageProfiles.RemoveAll(p => p.Id == id);
        if (Config.LastUsageProfileId == id) Config.LastUsageProfileId = null;
        Config.Save();
        _form.ShowProfiles();
    }

    public bool ApplyUsageProfile(string id, IWin32Window? owner)
    {
        var profile = Config.UsageProfiles.FirstOrDefault(p => p.Id == id);
        if (profile == null) return false;
        var s = profile.Settings;
        var next = new RouteRules(s.RouteDefault, s.AppRoutes) { SendVolumes = s.AppSendVolumes };
        if (!Config.RoutingNoticeAccepted && !Routing.Active && AppRouting.Supported && next.NeedsRouting)
        {
            if (MessageBox.Show(owner, L.F("此配置会启用按程序分流，声音会多约 {0} ms 延迟。要应用吗？", RoutedCapture.RoutedExtraLatencyMs),
                L.T("按程序分流会增加延迟"), MessageBoxButtons.YesNo, MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
            Config.RoutingNoticeAccepted = true;
        }
        _form.Flush();
        SavePendingVolume();
        _form.CancelPendingLatency();
        string? selectionBefore = OwnSession.KeyOf(Config);
        string connectionBefore = ConnectionSignature(Config);
        Fx.MicOn = false;
        UsageProfiles.Apply(Config, s, LastScan);
        Config.LastUsageProfileId = profile.Id; // 首页's picker shows it from now on
        Controller.ArrivalToRenderMs = Config.ArrivalToRenderMs;
        Controller.SetVolumeCap(Config.VolumeCapPercent);
        Controller.NightMode.Enabled = Config.NightMode;
        Fx.Apply();
        ApplyNightEq();
        Config.Save();
        Routing.Reload();
        UpdateMonitorGuard();
        UpdateVolumeKeys();
        _followLink?.CapChanged();
        _form.ReloadConfiguration();
        KickPlayers();
        // A profile without a speaker keeps the current one (UsageProfiles.Apply): no selection now means none before
        // either, and nothing to connect or disconnect.
        string? selection = OwnSession.KeyOf(Config);
        if (_wantConnected && selection != null && ConnectionSignature(Config) != connectionBefore)
            Connect(ProfileConnectTrigger(selection == selectionBefore, Controller.State is StreamState.Streaming or StreamState.Retrying));
        RaiseStateChanged();
        Log.Info("usage profile applied");
        return true;
    }

    /// <summary>
    /// What a connection is made with: a profile that changes none of it (only effects, night mode, routing, the
    /// capture device, the cap: all applied live) must not cut the sound for a reconnect.
    /// </summary>
    internal static string ConnectionSignature(AppConfig cfg) =>
        $"{OwnSession.KeyOf(cfg)}|{cfg.LatencyMs}|{cfg.FifoTargetMs}|{GroupForm.PlayLayout(cfg)}";

    /// <summary>
    /// The same speaker or group while our session is live: our own reconnect (never held back, never asks). Another
    /// speaker or group, or nothing live: like picking a speaker in the list, never taken over without a click.
    /// </summary>
    internal static ConnectTrigger ProfileConnectTrigger(bool sameSelection, bool live) =>
        sameSelection && live ? ConnectTrigger.Reconnect : ConnectTrigger.Automatic;
}
