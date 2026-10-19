using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal readonly record struct PlaybackCounters(object? Session, long BufferUnderruns, int? NetworkFluctuations);

/// <summary>Read-only playback counters and explicit troubleshooting actions.</summary>
internal sealed class DiagnosticsPage : ScrollPage
{
    private readonly MetricTile _underruns = new(L.T("缓冲欠载"));
    private readonly MetricTile _network = new(L.T("网络波动"));
    private readonly LayoutToolTip _tips = new();
    private readonly System.Windows.Forms.Timer _statsTimer = new() { Interval = 500 };
    private readonly Func<PlaybackCounters> _readCounters;
    private object? _session;
    private long _underrunsAtStart;

    public DiagnosticsPage(TrayApp app) : this(app.RunSyncTest, AppEnvironment.OpenLogFolder, app.ExportDiagnostics,
        () => ReadCounters(app)) { }

    internal DiagnosticsPage(Action<IWin32Window> runSyncTest, Action openLogFolder,
        Action<IWin32Window?> exportDiagnostics, Func<PlaybackCounters>? readCounters = null) : base(L.T("诊断"))
    {
        _readCounters = readCounters ?? (() => default);
        TitleText!.Wrap = true;
        var statistics = Ui.Section(L.T("播放统计"));
        var tiles = new TileGrid { MinTileWidth = 220 };
        tiles.Controls.AddRange([_underruns, _network]);
        _tips.SetToolTip(_underruns, L.T("电脑端音频缓冲供数不足的次数，不等于音箱实际中断的次数。"));
        _tips.SetToolTip(_network, L.T("根据网络延迟、丢包和重传估计，不代表实际播放中断；未监测时不显示数值。"));
        var sync = Ui.Section(L.T("音画同步测试"));
        var start = new FluentButton(L.T("开始"), ButtonKind.Primary);
        var logs = Ui.Section(L.T("日志"));
        var openLogs = new FluentButton(L.T("打开日志文件夹"), ButtonKind.Secondary, Glyph.Folder);
        var report = Ui.Section(L.T("诊断报告"));
        var export = new FluentButton(L.T("导出诊断"));
        // The raw log names speakers and contains addresses and paths; public reports use the existing redacted export.
        Content.Controls.AddRange([statistics, tiles,
            Ui.Note(L.T("本次连接的缓冲和网络统计，不代表实际听到的中断。")), sync,
            Ui.Note(L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。")), Ui.Row(8, null, start),
            logs, Ui.Note(L.F("完整的原始日志，自己排查问题时看。里面有音箱名称和 IP 地址，公开报告问题请改用下面的诊断报告。{0} 在这个文件夹里：\n{1}",
                AppConfig.LogFileName, AppEnvironment.LogFolder)), Ui.Row(8, null, openLogs),
            report, Ui.Note(L.T("公开报告问题时附上这个：脱敏后的日志，加上设备和网络统计。不包含配置文件、密码或录音，也不会上传。")),
            Ui.Row(8, null, export)]);
        Content.GapBefore[statistics] = 12;
        Content.GapBefore[sync] = 20;
        Content.GapBefore[logs] = Content.GapBefore[report] = 20;
        // Browsing this page must never start a tone, open Explorer or ask for an export destination.
        start.Click += (_, _) => runSyncTest(FindForm()!);
        openLogs.Click += (_, _) => openLogFolder();
        export.Click += (_, _) => exportDiagnostics(FindForm());
        _statsTimer.Tick += (_, _) => UpdateState();
    }

    private static PlaybackCounters ReadCounters(TrayApp app)
    {
        var controller = app.Controller;
        object? session = controller.State == StreamState.Streaming ? controller.AudioSessionIdentity : null;
        int? network = null;
        if (session != null && controller.HealthWatched && app.ReceiverSelection == null)
        {
            int arrivalToRender = controller.SafeLatency(0) - StreamController.SafetyMarginMs;
            network = controller.Health.Assess(StreamController.HealthClockMs(), controller.EffectiveLatencyMs,
                arrivalToRender, controller.RouterWatched, controller.PcWired).ConnectionThreats;
        }
        return new(session, controller.Fifo.Underruns, network);
    }

    // MainWindow also calls this on connection changes while the page is hidden: visiting the page must not
    // restart its counts, and a reconnect must not include the previous connection's FIFO underruns.
    public void UpdateState()
    {
        var counters = _readCounters();
        if (!ReferenceEquals(_session, counters.Session))
        {
            _session = counters.Session;
            _underrunsAtStart = counters.BufferUnderruns;
        }
        _underruns.SetValue(_session == null ? null : Math.Max(0, counters.BufferUnderruns - _underrunsAtStart).ToString());
        _network.SetValue(_session != null && counters.NetworkFluctuations is { } count ? count.ToString() : null);
    }

    public override void PageShown()
    {
        UpdateState();
        _statsTimer.Start();
    }

    public override void PageHidden() => _statsTimer.Stop();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statsTimer.Dispose();
            _tips.Dispose();
        }
        base.Dispose(disposing);
    }
}
