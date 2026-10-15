using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal sealed class GroupPage : ScrollPage
{
    private readonly TrayApp _app;
    private GroupPanel? _editor;
    private string? _signature;
    public GroupPage(TrayApp app) : base(L.T("多音箱（实验性）")) // experimental since 1.1; hardware untested
    {
        _app = app;
        TitleText!.Wrap = true;
        var apply = new FluentButton(L.T("应用"), ButtonKind.Primary);
        Content.Controls.Add(Ui.Row(8, null, apply));
        apply.Click += (_, _) =>
        {
            string before = GroupForm.PlayLayout(app.Config);
            _editor?.Apply();
            GroupForm.Save(app, before);
            _signature = null;
        };
    }
    public override void PageShown()
    {
        string signature = _app.Config.DeviceId + "|" + GroupForm.PlayLayout(_app.Config) + "|" +
            string.Join(";", _app.LastScan.Select(d => d.DeviceId + d.Address));
        if (signature == _signature) return;
        using var layout = LayoutPanel.BatchLayout(this);
        _editor?.Dispose();
        _editor = new GroupPanel(_app.Config);
        _editor.SetFound(_app.LastScan.ToList());
        Content.Controls.Add(_editor);
        Content.Controls.SetChildIndex(_editor, 1);
        _signature = signature;
    }
}
