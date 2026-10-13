using System.Globalization;
using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 设置 → 已配对的电视: the receivers paired with a PIN (Apple TV), each with 忘记配对. Hidden, header included, while there
/// are none: HomePods never need it. Read when the page is shown and whenever a pairing is saved or forgotten.
/// </summary>
internal sealed class PairingsPanel : StackPanel
{
    private readonly PairingStore _store;
    private readonly Action<string> _forget;
    private readonly TextBlock _header;
    private bool _shown;

    public PairingsPanel(TrayApp app, TextBlock header) : this(app.Pairings, app.ForgetPairing, header)
    {
    }

    /// <param name="header">The section header, hidden together with the panel.</param>
    internal PairingsPanel(PairingStore store, Action<string> forget, TextBlock header)
    {
        (_store, _forget, _header) = (store, forget, header);
        Gap = 4;
        _store.Changed += OnStoreChanged;
        ShowPairings();
    }

    public void PageShown()
    {
        _shown = true;
        ShowPairings();
    }

    public void PageHidden() => _shown = false;

    private void OnStoreChanged()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (!IsDisposed && _shown) ShowPairings();
            });
        }
        catch (InvalidOperationException) { } // the handle went away meanwhile
    }

    private void ShowPairings()
    {
        IReadOnlyList<PairedDevice> paired;
        try
        {
            paired = _store.List();
        }
        catch (Exception ex)
        {
            Log.Warn($"pairings: {ex.Message}");
            paired = [];
        }
        foreach (Control old in Controls.Cast<Control>().ToList())
        {
            Controls.Remove(old);
            old.Dispose();
        }
        foreach (var device in paired)
        {
            var forget = new FluentButton(L.T("忘记配对"));
            var row = new SettingRow(Glyph.Link, device.Name,
                L.F("配对于 {0}", device.PairedUtc.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)), forget);
            forget.AccessibleName = L.T("忘记配对") + " " + device.Name;
            forget.Click += (_, _) => Forget(device);
            Controls.Add(row);
        }
        Collapsed = paired.Count == 0;
        _header.Collapsed = paired.Count == 0;
        Relayout(this);
    }

    private void Forget(PairedDevice device)
    {
        if (MessageBox.Show(FindForm(), L.F("忘记与 {0} 的配对吗？下次连接时，需要在电视上重新输入数字。", device.Name), L.T("已配对的电视"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _forget(device.DeviceId);
        ShowPairings();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _store.Changed -= OnStoreChanged;
        base.Dispose(disposing);
    }
}
