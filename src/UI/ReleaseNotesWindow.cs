using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.UI;

/// <summary>A modeless window: closing it detaches the view, never the shared download.</summary>
internal sealed class ReleaseNotesWindow : Form, ISurface
{
    private readonly ScrollPage _notes = new() { Dock = DockStyle.Fill };
    private readonly StackPanel _footer = new() { Dock = DockStyle.Bottom, Inset = new Padding(24, 12, 24, 20), Gap = 8 };
    private readonly TextBlock _status = new(wrap: true);
    private readonly TextBlock _error = new(wrap: true, role: TextRole.Critical) { Collapsed = true };
    private readonly TextBlock _consent = new(wrap: true, role: TextRole.Secondary);
    private readonly DownloadProgress _progress = new();
    private readonly FluentButton _install = new(L.T("更新"), ButtonKind.Primary) { AlternateTexts = [L.T("更新"), L.T("取消")] };
    private readonly FluentButton _skip = new(L.T("跳过此版本"));
    private readonly FluentButton _later = new(L.T("稍后"));
    private readonly Action _update, _cancel, _skipUpdate;
    private readonly Release _release;
    private UpdateView? _view;

    public Color SurfaceColor => Theme.P.Window;
    internal Release Release => _release;
    internal FluentButton InstallButton => _install;
    internal FluentButton SkipButton => _skip;
    internal DownloadProgress ProgressBar => _progress;
    internal TextBlock StatusText => _status;
    internal TextBlock ConsentText => _consent;
    internal TextBlock ErrorText => _error;

    public ReleaseNotesWindow(Release release, NotesRange notes, string language, Action update, Action cancel, Action skip, Action<string> open)
    {
        _release = release; _update = update; _cancel = cancel; _skipUpdate = skip;
        Text = L.F("新版本 {0}", release.Version);
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Icon = Icons.App;
        Font = Theme.Font(TextStyle.Body, DeviceDpi);
        ClientSize = new Size(Theme.Dp(700, DeviceDpi), Theme.Dp(680, DeviceDpi));
        MinimumSize = new Size(Theme.Dp(480, DeviceDpi), Theme.Dp(420, DeviceDpi));
        _notes.Content.Controls.AddRange([_error, new TextBlock(Text, TextStyle.Title)]);
        foreach (var item in notes.Releases)
        {
            _notes.Content.Controls.Add(new TextBlock(item.Version.ToString(), TextStyle.Subtitle));
            if (item.PublishedAt is { } date)
                _notes.Content.Controls.Add(Ui.Note(L.F("发布日期：{0}", date.ToString("yyyy-MM-dd"))));
            var body = ReleaseNotes.ForLanguage(item.Body, language);
            if (string.IsNullOrWhiteSpace(body)) body = L.T("此版本没有提供更新说明。");
            foreach (var block in ReleaseNotes.Parse(body)) _notes.Content.Controls.Add(new NotesParagraph(block, open));
        }
        if (notes.EarlierChanges)
        {
            _notes.Content.Controls.Add(new TextBlock(L.T("更早的变化见发布页。"), wrap: true));
            var earlier = new FluentButton(L.T("在 GitHub 上查看"), ButtonKind.Subtle, Glyph.OpenInNew);
            earlier.Click += (_, _) => open(GitHubReleases.ReleasesPage);
            _notes.Content.Controls.Add(earlier);
        }
        var github = new FluentButton(L.T("在 GitHub 上查看"), ButtonKind.Subtle, Glyph.OpenInNew);
        github.Click += (_, _) => open(release.PageUrl);
        var spacer = new TextBlock();
        var buttons = Ui.Row(8, spacer, github, spacer, _skip, _later, _install);
        buttons.Wrap = true;
        _footer.Controls.AddRange([_consent, _progress, _status, buttons]);
        Controls.Add(_notes); Controls.Add(_footer);
        _install.Click += (_, _) => { if (_view?.Downloading == true) _cancel(); else _update(); };
        _skip.Click += (_, _) => { _skipUpdate(); Close(); };
        _later.Click += (_, _) => Close();
        Theme.Changed += ThemeChanged;
        ThemeChanged();
    }

    internal void ShowState(UpdateView view)
    {
        bool newError = view.ErrorDetail != null && view.ErrorDetail != _view?.ErrorDetail;
        _view = view;
        _status.Text = view.Status; _status.Role = view.Role;
        // Keep details in the scroll area: a nested exception must never push actions off-screen.
        _error.Text = view.ErrorDetail ?? ""; _error.Collapsed = view.ErrorDetail == null;
        if (newError) _notes.AutoScrollPosition = Point.Empty;
        _progress.Collapsed = !view.Downloading; _progress.Value = view.Progress;
        _install.Text = view.Downloading ? L.T("取消") : L.T("更新");
        _install.Kind = view.Downloading ? ButtonKind.Secondary : ButtonKind.Primary;
        _install.Enabled = view.Available == _release && !view.Checking;
        _skip.Enabled = _install.Enabled && !view.Downloading;
        _consent.Text = view.MsiInstall == false
            ? L.T("便携版：「更新」会打开发布页，请下载新的 zip 解压后替换。")
            : L.T("安装包会先用 SHA-256 校验，Windows 会请求管理员权限，装好后自动重启。正在推流时会中断约一分钟。");
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        if (_footer != null) _footer.Height = _footer.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        base.OnLayout(levent);
    }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.StyleWindow(this); }
    protected override void OnShown(EventArgs e)
    {
        var area = Screen.FromControl(this).WorkingArea;
        Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new Point(Math.Clamp(Left, area.Left, area.Right - Width), Math.Clamp(Top, area.Top, area.Bottom - Height));
        base.OnShown(e);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e) => LayoutPanel.ScaleForDpi(this, () =>
    {
        base.OnDpiChanged(e); Font = Theme.Font(TextStyle.Body, e.DeviceDpiNew);
    });
    private void ThemeChanged() { BackColor = Theme.P.Window; if (IsHandleCreated) Theme.StyleWindow(this); Invalidate(true); }
    protected override void Dispose(bool disposing) { if (disposing) Theme.Changed -= ThemeChanged; base.Dispose(disposing); }
}
