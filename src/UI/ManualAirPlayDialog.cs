using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed class ManualAirPlayDialog : FluentDialog
{
    private readonly TextBox _host = new() { AccessibleName = L.T("IP 地址") };
    private readonly TextBox _port = new() { AccessibleName = L.T("端口（默认 7000）") };
    private readonly TextBlock _status = Ui.Note();
    private readonly FluentButton _add = new(L.T("添加"), ButtonKind.Primary);
    private readonly CancellationTokenSource _cancel = new();
    public ManualAirPlayDevice? Device { get; private set; }

    public ManualAirPlayDialog() : base(L.T("手动添加 AirPlay 设备（实验性）")) // experimental since 1.1
    {
        Body.Controls.AddRange([
            Ui.Note(L.T("填写接收端的 IP 地址和端口；只读取设备信息。")),
            new TextBlock(L.T("IP 地址")), _host, new TextBlock(L.T("端口（默认 7000）")), _port, _status,
            ButtonRow(new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel }, _add),
        ]);
        AcceptButton = _add;
        _add.Click += async (_, _) =>
        {
            if (!ManualAirPlayDevice.TryEndpoint(_host.Text, _port.Text, out var address, out int port))
            {
                _status.Text = L.T("请填写有效的 IPv4 地址和 1–65535 之间的端口。");
                return;
            }
            _add.Enabled = false;
            _status.Text = L.T("读取中…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            IReadOnlyDictionary<string, object?> info = new Dictionary<string, object?>();
            try { info = await SpeakerInfoReader.ReadAsync(address!, port, timeout.Token); }
            catch (Exception ex) { Log.Info($"manual receiver info: {address}:{port} unreadable ({ex.Message})"); }
            if (_cancel.IsCancellationRequested || IsDisposed) return;
            Device = ManualAirPlayDevice.FromInfo(address!, port, info);
            DialogResult = DialogResult.OK;
        };
        FormClosed += (_, _) => _cancel.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cancel.Cancel(); _cancel.Dispose(); }
        base.Dispose(disposing);
    }
}
