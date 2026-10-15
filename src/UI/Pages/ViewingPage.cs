using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal sealed class ViewingPage : ScrollPage
{
    private readonly TrayApp _app;
    private readonly TextBlock _module = Ui.Note();
    public ViewingPage(TrayApp app) : base(L.T("同步观影窗口（实验性）")) // experimental since 1.1
    {
        _app = app;
        TitleText!.Wrap = true;
        if (!Players.VideoWindowModule.Offered) return;
        var open = new FluentButton(L.T("打开"), ButtonKind.Primary);
        var module = new FluentButton(L.T("选择模块…"));
        Content.Controls.AddRange([Ui.Note(L.T("延迟显示选定窗口，跟随 HomePod 音频；请关闭播放器和浏览器的其他同步补偿，避免重复调整。")),
            Ui.Row(8, null, module, open), _module]);
        module.Click += (_, _) => { app.ChooseVideoWindowModule(FindForm()); PageShown(); };
        open.Click += (_, _) => { app.OpenVideoWindow(FindForm()); PageShown(); };
    }
    public override void PageShown() => _module.Text = _app.Config.VideoWindowModuleDirectory ?? L.T("未设置");
}

internal sealed class SyncPage : ScrollPage
{
    public SyncPage(TrayApp app) : base(L.T("音画同步测试"))
    {
        var start = new FluentButton(L.T("开始"), ButtonKind.Primary);
        Content.Controls.AddRange([Ui.Note(L.T("听「咔」声、看闪光，测出声音比画面晚多少。需要先连接音箱。")), start]);
        // Browsing a page must never start a tone; the existing measurement starts only on this explicit action.
        start.Click += (_, _) => app.RunSyncTest(FindForm()!);
    }
}
