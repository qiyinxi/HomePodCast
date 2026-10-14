using HomePodCast.Net;
using HomePodCast.Protocol;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

/// <summary>
/// The one-time PIN pairing with a receiver that needs it (an Apple TV), opened only after the user's 连接 click
/// (TrayApp.Pairing.cs). 开始 asks the TV to show a PIN (that lights it up); the user types the 4 digits; a wrong PIN starts
/// over at once (the TV shows a new one). Network steps run off the UI thread; the dialog stays responsive and can be
/// cancelled except during the final exchange (under a second).
/// </summary>
internal sealed class PairingDialog : FluentDialog
{
    private enum Step { Intro, Starting, Pin, Finishing }

    private readonly Func<CancellationToken, Task<PinPairingSession>> _start;
    private readonly Action<HapPairing> _save;
    private readonly TextBlock _status = new("", TextStyle.Body, TextRole.Secondary, wrap: true);
    private readonly TextBlock _pinLabel = new(L.T("输入电视上显示的 4 位数字"));
    private readonly TextBox _pin;
    private readonly RowPanel _pinRow;
    private readonly FluentButton _action = new(L.T("开始"), ButtonKind.Primary) { MinWidth = 96 };
    private readonly FluentButton _cancel = new(L.T("取消")) { DialogResult = DialogResult.Cancel, MinWidth = 96 };
    private CancellationTokenSource? _starting;
    private PinPairingSession? _session;
    private Step _step = Step.Intro;
    private readonly bool _thirdParty;

    /// <param name="owner">The main window when it is visible; null from the tray (the dialog then stays on top).</param>
    /// <param name="start">Asks the receiver to show a PIN and begins pair-setup (PinPairingSession.StartAsync).</param>
    /// <param name="save">Stores the finished pairing (UI thread); may throw.</param>
    /// <returns>True: paired and stored.</returns>
    public static bool Run(IWin32Window? owner, string speaker, PairingReason reason,
        Func<CancellationToken, Task<PinPairingSession>> start, Action<HapPairing> save, bool thirdParty = false)
    {
        using var dialog = new PairingDialog(speaker, reason, start, save, thirdParty);
        if (owner != null) return dialog.ShowDialog(owner) == DialogResult.OK;
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.TopMost = true;
        return dialog.ShowDialog() == DialogResult.OK;
    }

    private PairingDialog(string speaker, PairingReason reason, Func<CancellationToken, Task<PinPairingSession>> start,
        Action<HapPairing> save, bool thirdParty = false) : base(thirdParty
            ? L.T("AirPlay 设备配对（实验性，未在实机上测试）") // experimental since 1.1
            : L.T("在电视上配对"))
    {
        _start = start;
        _thirdParty = thirdParty;
        _save = save;
        ContentWidth = 460;
        var rejected = new TextBlock(reason == PairingReason.PairingRejected
            ? thirdParty ? L.T("这台接收端上的配对已失效，请重新配对。")
                : L.T("电视上的配对已失效（Apple TV 可能重置过，或在设置里移除了这台电脑）。") : "", wrap: true);
        var intro = new TextBlock(thirdParty
            ? L.F("配对「{0}」：点「开始」后，在接收端查看 4 位数字并填写到这里。", speaker)
            : L.F("要在电视上配对 {0}：点「开始」后，电视会显示 4 位数字，把它输入到这里。", speaker), wrap: true);
        var note = Ui.Note(thirdParty
            ? L.T("配对只需一次。只接受 MFi 认证的接收端不受支持。")
            : L.T("配对只需一次。之后推送声音时，Apple TV 可能会顺带打开电视。"));
        _pin = new TextBox
        {
            MaxLength = 4,
            TextAlign = HorizontalAlignment.Center,
            BorderStyle = BorderStyle.FixedSingle,
            AccessibleName = L.T("输入电视上显示的 4 位数字"),
            ImeMode = ImeMode.Disable,
        };
        if (thirdParty) _pinLabel.Text = _pin.AccessibleName = L.T("输入接收端显示的 4 位数字");
        _pinRow = Ui.Row(12, null, _pinLabel, _pin);
        _pinRow.FixedWidths[_pin] = 96;
        _pinRow.Collapsed = true;
        var buttons = ButtonRow(_action, _cancel);
        Body.Controls.AddRange([rejected, intro, note, _pinRow, _status, buttons]);
        Body.GapBefore[buttons] = 20;
        AcceptButton = _action;
        CancelButton = _cancel;
        ActiveControl = _action;
        ApplyPinColors();

        _pin.KeyPress += (_, e) =>
        {
            if (!char.IsControl(e.KeyChar) && !char.IsAsciiDigit(e.KeyChar)) e.Handled = true;
        };
        _pin.TextChanged += (_, _) =>
        {
            var digits = new string(_pin.Text.Where(char.IsAsciiDigit).ToArray());
            if (digits != _pin.Text) _pin.Text = digits; // pasted text
            UpdateAction();
        };
        _action.Click += (_, _) => OnAction();
        PerformLayout();
    }

    private void OnAction()
    {
        switch (_step)
        {
            case Step.Intro:
                StartPairing();
                break;
            case Step.Pin when _pin.Text.Length == 4:
                FinishPairing(_pin.Text);
                break;
        }
    }

    /// <summary>Asks the TV for a PIN (again after a wrong one or an error).</summary>
    /// <param name="notice">Shown (as an error) next to the PIN box once the new PIN is up: why it is asked again.</param>
    private async void StartPairing(string? notice = null)
    {
        CloseSession();
        _step = Step.Starting;
        _pinRow.Collapsed = true;
        _pin.Clear();
        ShowStatus(_thirdParty ? L.T("正在请接收端显示数字…") : L.T("正在请电视显示数字…"), TextRole.Secondary);
        UpdateAction();
        var cts = _starting = new CancellationTokenSource();
        try
        {
            var session = await Task.Run(() => _start(cts.Token));
            if (IsDisposed || cts.IsCancellationRequested)
            {
                session.Dispose();
                return;
            }
            _session = session;
            _step = Step.Pin;
            _pinRow.Collapsed = false;
            ShowStatus(notice ?? "", notice != null ? TextRole.Critical : TextRole.Secondary);
            UpdateAction();
            _pin.Focus();
        }
        catch (Exception ex)
        {
            if (IsDisposed || cts.IsCancellationRequested) return;
            Log.Warn($"pairing: could not start: {ex.Message}");
            ShowError(Describe(ex, starting: true));
        }
        finally
        {
            if (ReferenceEquals(_starting, cts)) _starting = null;
            cts.Dispose();
        }
    }

    private async void FinishPairing(string pin)
    {
        var session = _session;
        if (session == null) return;
        _session = null;
        _step = Step.Finishing;
        ShowStatus(L.T("正在配对…"), TextRole.Secondary);
        UpdateAction();
        try
        {
            var pairing = await Task.Run(() => session.Finish(pin));
            _save(pairing);
            DialogResult = DialogResult.OK;
        }
        catch (HapPairingException ex) when (ex.Error == HapError.Authentication)
        {
            Log.Info("pairing: wrong PIN; asking the TV for a new one");
            if (IsDisposed) return;
            StartPairing(L.T("数字不对。电视上会显示新的数字，请重新输入。"));
        }
        catch (Exception ex)
        {
            Log.Warn($"pairing: failed: {ex.Message}");
            if (IsDisposed) return;
            ShowError(Describe(ex, starting: false));
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>An error: 开始 becomes 重试, which starts over with a new PIN.</summary>
    private void ShowError(string text)
    {
        CloseSession();
        _step = Step.Intro;
        _pinRow.Collapsed = true;
        ShowStatus(text, TextRole.Critical);
        UpdateAction();
    }

    /// <summary>What went wrong, in the user's words.</summary>
    /// <param name="starting">While asking the TV for a PIN (else while finishing the pairing).</param>
    internal static string Describe(Exception ex, bool starting) => ex switch
    {
        HapPairingException { Error: HapError.BackOff, RetryAfterSeconds: { } s } => L.F("电视暂时不接受配对，请 {0} 秒后再试。", s),
        HapPairingException { Error: HapError.BackOff } or HapPairingException { Error: HapError.Busy } =>
            L.T("电视暂时不接受配对，请稍后再试。"),
        HapPairingException { Error: HapError.MaxTries } => L.T("输错次数太多，电视暂时锁定了配对。请稍后再试，或重启 Apple TV。"),
        HapPairingException { Error: HapError.MaxPeers } => L.T("电视上配对的设备已满，请先在 Apple TV 的设置里移除一些设备。"),
        HapPairingException { Error: HapError.Unavailable } => L.T("电视现在不接受配对，请稍后再试。"),
        System.Net.Sockets.SocketException or IOException or OperationCanceledException => L.T("连不上电视（是否开着、和电脑在同一网络？）"),
        _ => starting ? L.F("无法开始配对：{0}", ex.Message) : L.F("配对失败：{0}", ex.Message),
    };

    private void ShowStatus(string text, TextRole role)
    {
        _status.Text = text;
        _status.Role = role;
        PerformLayout();
    }

    private void UpdateAction()
    {
        _action.Text = _step == Step.Intro && _status.Role == TextRole.Critical ? L.T("重试")
            : _step is Step.Pin or Step.Finishing ? L.T("配对") : L.T("开始");
        _action.Enabled = _step == Step.Intro || _step == Step.Pin && _pin.Text.Length == 4;
        _cancel.Enabled = _step != Step.Finishing;
        PerformLayout();
    }

    private void CloseSession()
    {
        _starting?.Cancel();
        _session?.Dispose();
        _session = null;
    }

    private void ApplyPinColors()
    {
        _pin.BackColor = Theme.P.Control;
        _pin.ForeColor = Theme.P.Text;
        _pin.Font = Theme.Font(TextStyle.Subtitle, DeviceDpi);
    }

    protected override void OnThemeChanged() => ApplyPinColors();

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        _pin.Font = Theme.Font(TextStyle.Subtitle, e.DeviceDpiNew);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The last exchange is short and leaves the pairing stored on the TV: let it finish.
        if (_step == Step.Finishing && DialogResult != DialogResult.OK) e.Cancel = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) CloseSession();
        base.Dispose(disposing);
    }
}
