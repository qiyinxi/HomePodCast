using HomePodCast.UI.Controls;

namespace HomePodCast.UI;

internal sealed class OutputGroupNameDialog : FluentDialog
{
    private readonly TextBox _name = new() { MaxLength = OutputGroups.MaxNameLength, AccessibleName = L.T("分组名称"), BorderStyle = BorderStyle.FixedSingle };
    internal string GroupName => _name.Text.Trim();
    internal OutputGroupNameDialog(string initial = "") : base(L.T("保存分组"))
    {
        ContentWidth = 460;
        var save = new FluentButton(L.T("保存"), ButtonKind.Primary) { DialogResult = DialogResult.OK };
        var cancel = new FluentButton(L.T("取消")) { DialogResult = DialogResult.Cancel };
        _name.BackColor = Theme.P.Control; _name.ForeColor = Theme.P.Text; _name.Text = initial;
        Body.Controls.AddRange([Ui.Note(L.T("只保存输出和同步设置，不改变音源、音效或当前连接。")),
            new TextBlock(L.T("分组名称")), _name, ButtonRow(cancel, save)]);
        _name.TextChanged += (_, _) => save.Enabled = GroupName.Length > 0;
        save.Enabled = GroupName.Length > 0; AcceptButton = save; CancelButton = cancel; ActiveControl = _name;
    }
}
