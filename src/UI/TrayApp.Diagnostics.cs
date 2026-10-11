using System.Runtime.InteropServices;
using HomePodCast.Net;

namespace HomePodCast.UI;

internal sealed partial class TrayApp
{
    private readonly Queue<DateTimeOffset> _glitchMarkers = new();
    private DateTimeOffset? _lastGlitchMarker;

    public void MarkAudibleGlitch()
    {
        if (Controller.State != StreamState.Streaming) return;
        var now = DateTimeOffset.Now;
        if (_lastGlitchMarker is { } last && now - last < TimeSpan.FromSeconds(1)) return;
        _lastGlitchMarker = now;
        _glitchMarkers.Enqueue(now);
        while (_glitchMarkers.Count > 100) _glitchMarkers.Dequeue();
        Log.Info($"audible glitch marked by user at {now:O}: latency={Controller.EffectiveLatencyMs}ms " +
            $"fifoTarget={Controller.Fifo.TargetMs:F0}ms underruns={Controller.Fifo.Underruns} " +
            $"rtxMiss={Controller.ActiveSender?.RetransmitMisses ?? 0}");
        _tray.ShowBalloonTip(2000, "HomePodCast", L.T("已记录卡顿时间，可导出诊断报告。"), ToolTipIcon.Info);
    }

    public async void ExportDiagnostics(IWin32Window? owner)
    {
        using var picker = new SaveFileDialog
        {
            Title = L.T("导出诊断报告"), Filter = "ZIP (*.zip)|*.zip", DefaultExt = "zip", AddExtension = true,
            FileName = $"HomePodCast-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip", OverwritePrompt = true,
        };
        if (picker.ShowDialog(owner) != DialogResult.OK) return;
        try // async void: nothing may escape
        {
            var (snapshot, identifiers) = DiagnosticInputs();
            string destination = picker.FileName;
            string log = Path.Combine(AppConfig.Directory, AppConfig.LogFileName);
            await Task.Run(() => DiagnosticReports.Export(destination, snapshot, identifiers, log, log + ".old"));
            if (!_quitting) MessageBox.Show(owner, L.T("诊断报告已保存。分享前可以检查其中的内容。"), L.T("导出诊断报告"));
        }
        catch (Exception ex)
        {
            Log.Warn($"diagnostic export failed: {ex.Message}");
            if (!_quitting) MessageBox.Show(owner, L.T("导出失败，请检查保存位置后重试。"), L.T("导出诊断报告"));
        }
    }

    /// <summary>
    /// The report's measurements, and the names and ids the settings know (the speakers, the capture, mic and monitor
    /// devices, the network, the PC) for the redaction to look for besides what the log marked. Profile names are not
    /// among them: they are the user's labels for setups, not names of devices or places.
    /// </summary>
    private (DiagnosticSnapshot Snapshot, List<string?> Identifiers) DiagnosticInputs()
    {
        var c = Controller;
        var sender = c.ActiveSender;
        var capture = c.Capture;
        var info = c.Client?.Info;
        var network = c.PcNetwork;
        var verdict = c.HealthWatched ? c.Health.Assess(StreamController.HealthClockMs(), c.EffectiveLatencyMs,
            c.SafeLatency(0) - StreamController.SafetyMarginMs, c.RouterWatched, c.PcWired) : null;
        var snapshot = new DiagnosticSnapshot(DateTimeOffset.Now, Update.ReleaseVersion.CurrentText,
            RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription, c.State,
            info?.GetValueOrDefault("model")?.ToString(), info?.GetValueOrDefault("sourceVersion")?.ToString(),
            Routing.Active, Config.CaptureDeviceId != null, Config.LatencyMs, c.EffectiveLatencyMs, VideoDelayMs,
            c.ArrivalToRenderMs, c.Fifo.TargetMs, c.Fifo.Underruns, c.Fifo.IdleGaps, c.Fifo.Overflows,
            sender?.PacketsSent, sender?.LateWakeups, sender?.SkippedPackets, sender?.RetransmitRequests,
            sender?.Retransmitted, sender?.RetransmitMisses, network?.Kind ?? AdapterKind.Other,
            network?.Category ?? NetworkCategory.Unknown, capture?.CaptureDeviceMissing ?? false,
            capture?.NoOutputDevice ?? false, verdict, _glitchMarkers.ToArray());
        var identifiers = new List<string?>
        {
            Config.DeviceName, Config.Host, Config.MultiRoomDeviceName, Config.MultiRoomHost, Config.CaptureDeviceId,
            Config.CaptureDeviceName, capture?.DeviceName, capture?.EndpointId, Config.Effects.MicDeviceId,
            Config.Effects.MonitorDeviceId, network?.Name, network?.Interface, info?.GetValueOrDefault("name")?.ToString(),
            Environment.UserName, Environment.MachineName, AppConfig.Directory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        identifiers.AddRange(DiagnosticReports.IdForms(Config.DeviceId));
        identifiers.AddRange(DiagnosticReports.IdForms(Config.MultiRoomDeviceId));
        foreach (var s in Config.UsageProfiles.Select(p => p.Settings))
        {
            identifiers.AddRange([s.DeviceName, s.Host, s.CaptureDeviceId, s.CaptureDeviceName, s.MultiRoomDeviceName,
                s.MultiRoomHost, s.Effects.MicDeviceId, s.Effects.MonitorDeviceId]);
            identifiers.AddRange(DiagnosticReports.IdForms(s.DeviceId));
            identifiers.AddRange(DiagnosticReports.IdForms(s.MultiRoomDeviceId));
        }
        return (snapshot, identifiers);
    }
}
