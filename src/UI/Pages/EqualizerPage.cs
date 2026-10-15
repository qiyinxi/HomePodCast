using HomePodCast.Audio;
using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

/// <summary>EQ for all sound streamed to the HomePod, independent of microphone monitoring.</summary>
internal sealed class EqualizerPage : ScrollPage
{
    private readonly AppConfig _config;
    private readonly MicEffects _fx;
    private readonly EqEditor _outputEq = new(L.T("HomePod 输出均衡"));
    private readonly TextBlock _nightNote = Ui.Note();
    private readonly System.Windows.Forms.Timer _saveDebounce = new() { Interval = 600 };

    public EqualizerPage(AppConfig config, MicEffects fx) : base(L.T("均衡"))
    {
        _config = config;
        _fx = fx;
        _outputEq.Card.Controls.Add(Ui.Note(L.T("作用于推送到 HomePod 的所有声音，包括电脑声音和麦克风。")));
        _outputEq.Card.Controls.Add(_nightNote);
        Content.Controls.Add(_outputEq.Card);
        _outputEq.Changed += () =>
        {
            _fx.Apply();
            _saveDebounce.Stop();
            _saveDebounce.Start();
        };
        _saveDebounce.Tick += (_, _) => Flush();
        ReloadConfiguration();
    }

    public void ReloadConfiguration()
    {
        _saveDebounce.Stop();
        _outputEq.Load(_config.Effects.OutputEq ??= new EqSettings());
        ShowNightMode();
    }

    public void ShowNightMode() => _nightNote.Text = _config.NightMode
        ? L.T("夜间模式开着：现在用的是「减弱低音」，关掉夜间模式后恢复这里的设置。") : "";

    public override void PageShown() => ShowNightMode();

    /// <summary>Include pending EQ edits when quitting or saving/switching a usage profile.</summary>
    public void Flush()
    {
        if (!_saveDebounce.Enabled) return;
        _saveDebounce.Stop();
        _config.Save();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
            _saveDebounce.Dispose();
        }
        base.Dispose(disposing);
    }
}
