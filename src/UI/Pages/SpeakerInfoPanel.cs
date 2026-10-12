using HomePodCast.Net;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 设置 → 音箱信息: for the selected speaker (each member of a stereo pair or multi-room group) its model in plain words,
/// whether someone is playing to it, system and AirPlay version, IP address, Wi-Fi MAC address (to find it in the router's
/// client list: which band it is on, or to give it a fixed IP), Bluetooth address and device ID; and the speaker's own
/// processing time saved from the last connection. Read from GET /info when the page is shown (unless read in the last
/// 30 s) and on 刷新, off the UI thread with a short timeout; the TXT record of the last scan fills in until then.
/// </summary>
internal sealed class SpeakerInfoPanel : StackPanel
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly SpeakerInfoCache _cache;
    private readonly Func<List<SelectedSpeaker>> _speakers;
    private readonly Func<bool> _streaming;
    private readonly Func<int?> _processingMs;
    private readonly Func<IReadOnlyList<AirPlayDevice>> _scan;
    private readonly TextBlock _none = Ui.Note();
    private readonly StackPanel _cards = new() { Gap = 8 };
    private readonly TextBlock _processing = Ui.Note();
    private readonly FluentButton _refresh = new(L.T("刷新"), ButtonKind.Secondary, Glyph.Refresh);
    private readonly Dictionary<string, SpeakerCard> _byKey = new();
    private bool _shown;

    public SpeakerInfoPanel(TrayApp app) : this(app.SpeakerInfo, app.SelectedSpeakers,
        () => app.Controller.State == StreamState.Streaming, () => app.Config.ArrivalToRenderMs, () => app.LastScan)
    {
    }

    /// <param name="speakers">The selected speakers now.</param>
    /// <param name="streaming">This PC streams to them now.</param>
    /// <param name="processingMs">The speaker's processing time saved from the last connection.</param>
    /// <param name="scan">The last scan as found (pairs not merged): the other devices of a speaker's group.</param>
    internal SpeakerInfoPanel(SpeakerInfoCache cache, Func<List<SelectedSpeaker>> speakers, Func<bool> streaming, Func<int?> processingMs,
        Func<IReadOnlyList<AirPlayDevice>>? scan = null)
    {
        (_cache, _speakers, _streaming, _processingMs) = (cache, speakers, streaming, processingMs);
        _scan = scan ?? (() => []);
        Gap = 8;
        _refresh.AccessibleName = L.T("重新读取音箱信息");
        Controls.AddRange([_none, _cards, Ui.Row(12, _processing, _processing, _refresh)]);
        _refresh.Click += (_, _) => Refresh(force: true);
        _cache.Changed += OnInfoChanged;
    }

    public void PageShown()
    {
        _shown = true;
        Refresh(force: false);
    }

    public void PageHidden() => _shown = false;

    /// <summary>
    /// The selection may have changed while the page is shown (a profile applied from 设置 → 使用配置): the cards follow at
    /// once and a speaker not read yet is read. While hidden, nothing: <see cref="PageShown"/> does it.
    /// </summary>
    public void SelectionChanged()
    {
        if (_shown) Refresh(force: false);
    }

    /// <summary>Shows what is known and reads every selected speaker whose /info is missing, failed or older than 30 s.</summary>
    private void Refresh(bool force)
    {
        var speakers = _speakers();
        ShowAll(speakers);
        foreach (var s in speakers)
        {
            var entry = _cache.Get(s.DeviceId);
            bool fresh = entry is { State: SpeakerInfoState.Ready, ReadUtc: { } at } && DateTime.UtcNow - at < MaxAge
                         || entry is { State: SpeakerInfoState.Reading };
            if (force || !fresh) _ = _cache.RefreshAsync(s.DeviceId, s.Name, s.Address);
        }
    }

    /// <summary>A read started or finished (thread pool).</summary>
    private void OnInfoChanged(string key)
    {
        if (!_shown || IsDisposed || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (_shown && !IsDisposed) ShowAll(_speakers());
            });
        }
        catch (InvalidOperationException) { } // the handle went away meanwhile
    }

    private void ShowAll(List<SelectedSpeaker> speakers)
    {
        _none.Text = speakers.Count == 0 ? L.T("还没有选择音箱") : "";
        var keys = speakers.Select(s => SpeakerInfoCache.Key(s.DeviceId)).ToList();
        foreach (var gone in _byKey.Keys.Except(keys).ToList())
        {
            _byKey[gone].Dispose();
            _byKey.Remove(gone);
        }
        bool streaming = _streaming();
        var scan = _scan();
        for (int i = 0; i < speakers.Count; i++)
        {
            if (!_byKey.TryGetValue(keys[i], out var card))
            {
                _byKey[keys[i]] = card = new SpeakerCard();
                _cards.Controls.Add(card);
            }
            _cards.Controls.SetChildIndex(card, i);
            card.Show(speakers[i], _cache.Get(speakers[i].DeviceId), streaming, scan);
        }
        _processing.Text = _processingMs() is { } ms ? L.F("上次连接时的音箱处理时间：{0} ms", ms) : "";
        Relayout(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cache.Changed -= OnInfoChanged;
        base.Dispose(disposing);
    }

    /// <summary>One speaker: its name and the read's state, then a caption / value line per known field.</summary>
    private sealed class SpeakerCard : Card
    {
        private readonly TextBlock _name = new("", TextStyle.BodyStrong);
        private readonly TextBlock _state = new("", TextStyle.Caption, TextRole.Secondary) { Align = HorizontalAlignment.Right };
        private readonly TextBlock _model = Value(), _session = Value(), _group = Value(), _system = Value(), _airplay = Value(),
            _address = Value(), _mac = new(), _bluetooth = Value(), _deviceId = Value();
        private readonly TextBlock _macNote = Ui.Note(L.T("在路由器的设备列表里按这个地址找到 HomePod，就能看到它连在 2.4G 还是 5G，也可以给它分配固定 IP。"));
        private readonly FluentButton _copy = new(L.T("复制"), ButtonKind.Secondary, Glyph.Copy) { AccessibleName = L.T("复制 Wi-Fi MAC 地址") };
        private readonly RowPanel _macRow;
        private readonly InfoLines _lines = new();
        private string? _macText;

        public SpeakerCard()
        {
            Gap = 10;
            _macRow = Ui.Row(8, null, _mac, _copy);
            _lines.Add(L.T("型号"), _model);
            _lines.Add(L.T("状态"), _session);
            _lines.Add(L.T("分组"), _group);
            _lines.Add(L.T("系统版本"), _system);
            _lines.Add(L.T("AirPlay 版本"), _airplay);
            _lines.Add(L.T("IP 地址"), _address);
            _lines.Add(L.T("Wi-Fi MAC 地址"), _macRow);
            _lines.Add("", _macNote);
            _lines.Add(L.T("蓝牙地址"), _bluetooth);
            _lines.Add(L.T("设备 ID"), _deviceId);
            Controls.Add(Ui.Row(12, _name, _name, _state));
            Controls.Add(_lines);
            _copy.Click += (_, _) =>
            {
                if (_macText == null) return;
                try { Clipboard.SetText(_macText); }
                catch (Exception ex) { Log.Warn($"clipboard: {ex.Message}"); }
            };
        }

        public void Show(SelectedSpeaker speaker, SpeakerInfoEntry? entry, bool streaming, IReadOnlyList<AirPlayDevice> scan)
        {
            var d = SpeakerDetails.Build(speaker.Name, speaker.Address, speaker.Txt, entry?.Info);
            _name.Text = d.Name;
            (_state.Text, _state.Role) = ReadState(entry);
            Set(_model, d.ModelText);
            var txt = TxtRecord.FromInfo(entry?.Info) ?? speaker.Txt;
            var holder = HomeTheater.IsAppleTvModel(d.Model) ? null : HomeTheater.HolderOf(GroupMembership.FromTxt(txt), scan);
            Set(_session, SessionText(d.SessionActive, streaming, holder?.Name));
            Set(_group, GroupText(GroupRole.Of(d.Model, txt, scan)));
            Set(_system, d.SystemVersion);
            Set(_airplay, d.AirPlayVersion);
            Set(_address, d.Address);
            _macText = d.WifiMac;
            _mac.Text = d.WifiMac ?? "";
            _copy.Enabled = d.WifiMac != null; // also keeps a hidden button out of the tab order
            _lines.Show(_macRow, d.WifiMac != null);
            _lines.Show(_macNote, d.WifiMac != null);
            // A Bluetooth privacy address: three different ones were read from the same HomePod on 2026-10-11.
            Set(_bluetooth, d.BluetoothAddress is { } bluetooth ? L.F("{0}（会定期变化）", bluetooth) : null);
            Set(_deviceId, d.DeviceId);
            Relayout(this);
        }

        /// <summary>A value that wraps in a narrow window (the status line is long in some languages).</summary>
        private static TextBlock Value() => new("", TextStyle.Body, TextRole.Primary, wrap: true);

        /// <summary>A field's value; an unknown one leaves its line out.</summary>
        private void Set(TextBlock value, string? text)
        {
            value.Text = text ?? "";
            _lines.Show(value, text != null);
        }

        internal static (string Text, TextRole Role) ReadState(SpeakerInfoEntry? entry) => entry switch
        {
            null or { State: SpeakerInfoState.Reading } => (L.T("读取中…"), TextRole.Secondary),
            { State: SpeakerInfoState.Failed, Info: null } => (L.T("无法读取（音箱不在线？）"), TextRole.Caution),
            { State: SpeakerInfoState.Failed } => (L.T("无法读取（音箱不在线？），下面是上次读到的信息"), TextRole.Caution),
            _ => ("", TextRole.Secondary),
        };

        /// <summary>The speaker's group in words (null: none, the line is left out).</summary>
        internal static string? GroupText(GroupRole role) => role switch
        {
            { Kind: GroupRoleKind.HomeTheaterSpeaker, Name: { } tv } => L.F("家庭影院：Apple TV「{0}」的音箱", tv),
            { Kind: GroupRoleKind.HomeTheaterSpeaker } => L.T("家庭影院：Apple TV 的音箱"),
            { Kind: GroupRoleKind.HomeTheaterLeader, Name: { } speakers } => L.F("家庭影院：{0}", speakers),
            { Kind: GroupRoleKind.HomeTheaterLeader } => L.T("家庭影院"),
            { Kind: GroupRoleKind.StereoPair, Name: { } pair } => L.F("立体声对「{0}」", pair),
            { Kind: GroupRoleKind.StereoPair } => L.T("立体声对"),
            _ => null,
        };

        /// <summary>
        /// The active-session flag in words (null: not read yet); while this PC streams, the session is ours. A HomePod held
        /// by its home-theater Apple TV (<paramref name="appleTv"/>) names it.
        /// </summary>
        internal static string? SessionText(bool? active, bool streaming, string? appleTv = null) => streaming ? L.T("这台电脑正在推送")
            : active switch
            {
                true when appleTv != null => L.F("Apple TV「{0}」正连着它（家庭影院）", appleTv),
                true => L.T("其他设备正在推送（比如 iPhone 或 Mac）"),
                false => L.T("空闲"),
                null => null,
            };
    }

    /// <summary>
    /// Caption / value lines, the captions (secondary text) in one column as wide as the widest, each line as tall as its
    /// taller part; a hidden line takes no space.
    /// </summary>
    private sealed class InfoLines : LayoutPanel
    {
        private readonly List<(TextBlock Caption, Control Value)> _lines = [];
        private readonly HashSet<Control> _hidden = [];

        public void Add(string caption, Control value)
        {
            var text = new TextBlock(caption, TextStyle.Body, TextRole.Secondary);
            Controls.Add(text);
            Controls.Add(value);
            value.AccessibleName ??= caption;
            _lines.Add((text, value));
        }

        /// <summary>Shows or hides <paramref name="value"/>'s line (the caller lays the card out again).</summary>
        public void Show(Control value, bool show)
        {
            if (show) _hidden.Remove(value);
            else _hidden.Add(value);
        }

        protected override int Arrange(int width, bool apply)
        {
            int captionWidth = _lines.Max(l => WidthFor(l.Caption));
            int gap = Dp(16), valueWidth = Math.Max(0, width - captionWidth - gap);
            int y = 0;
            bool first = true;
            foreach (var (caption, value) in _lines)
            {
                if (_hidden.Contains(value))
                {
                    if (apply)
                    {
                        Place(caption, 0, y, 0, 0);
                        Place(value, 0, y, 0, 0);
                    }
                    continue;
                }
                int ch = HeightFor(caption, captionWidth), vh = HeightFor(value, valueWidth), h = Math.Max(ch, vh);
                if (!first) y += Dp(6);
                if (apply)
                {
                    Place(caption, 0, y + (h - ch) / 2, captionWidth, ch);
                    Place(value, captionWidth + gap, y + (h - vh) / 2, valueWidth, vh);
                }
                y += h;
                first = false;
            }
            return y;
        }
    }
}
