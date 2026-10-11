using HomePodCast.Audio;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    public void ShowProfiles(IWin32Window owner)
    {
        using var dialog = new ProfilesForm(this);
        dialog.ShowDialog(owner);
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
        Fx.MicOn = false;
        UsageProfiles.Apply(Config, s);
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
        if (_wantConnected && Config.DeviceId != null) Connect();
        else if (_wantConnected) Disconnect();
        RaiseStateChanged();
        Log.Info("usage profile applied");
        return true;
    }
}
