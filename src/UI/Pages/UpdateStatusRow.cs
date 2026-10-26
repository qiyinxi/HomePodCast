using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.UI.Pages;

/// <summary>
/// 设置 → 软件更新's first row, one row in every state as in Windows Update: 当前版本 X with the check's status under it,
/// and on the right 「检查更新」, or while a new version is offered 「更新」「查看说明」「跳过此版本」 in its place (「更新」
/// turns into 「取消」 while it downloads). Only what is in it changes: no row appears or goes away below it (2026-10-14,
/// the maintainer: a second 「新版本 X」 row that came and went made the section jump). What 「更新」 does is its tooltip
/// and accessible description, not a second line under the status: the notes window explains it before installing.
/// The Microsoft Store version: the status only.
/// </summary>
internal sealed class UpdateStatusRow
{
    private readonly ToolTip _tips = new();

    /// <param name="install">「更新」, also while it reads 「取消」 (the caller cancels the download then).</param>
    public UpdateStatusRow(Action check, Action install, Action notes, Action skip)
    {
        Buttons = Ui.Row(8, null, Check, Install, Notes, Skip);
        Buttons.Wrap = true; // under the text at the minimum width, the German texts need two lines
        Row = new SettingRow(Glyph.UpdateRestore, L.F("当前版本 {0}", ReleaseVersion.CurrentText), "", Buttons);
        Check.Click += (_, _) => check();
        Install.Click += (_, _) => install();
        Notes.Click += (_, _) => notes();
        Skip.Click += (_, _) => skip();
        Row.Disposed += (_, _) => _tips.Dispose();
    }

    public SettingRow Row { get; }
    internal RowPanel Buttons { get; }
    internal FluentButton Check { get; } = new(L.T("检查更新"));
    internal FluentButton Install { get; } = new(L.T("更新"), ButtonKind.Primary) { MinWidth = 72, AlternateTexts = [L.T("更新"), L.T("取消")] };
    internal FluentButton Notes { get; } = new(L.T("查看说明"), ButtonKind.Secondary, Glyph.OpenInNew);
    internal FluentButton Skip { get; } = new(L.T("跳过此版本"), ButtonKind.Subtle);

    /// <summary>The tooltip of 「更新」 (null while it reads 「取消」).</summary>
    internal string? InstallTip => _tips.GetToolTip(Install) is { Length: > 0 } tip ? tip : null;

    public void Show(UpdateView u)
    {
        var status = Row.DescriptionText;
        status.Text = u.Status;
        status.Role = u.Role;
        // The Microsoft Store version: the Store updates it; nothing here contacts GitHub.
        Buttons.Collapsed = u.Store;
        if (u.Store) return;

        bool offered = u.Available != null;
        Check.Collapsed = offered;
        Install.Collapsed = Notes.Collapsed = Skip.Collapsed = !offered;
        Check.Enabled = !u.Checking && !u.Downloading;
        Install.Text = u.Downloading ? L.T("取消") : L.T("更新");
        Install.Kind = u.Downloading ? ButtonKind.Secondary : ButtonKind.Primary;
        Install.Enabled = !u.Checking;
        Skip.Enabled = !u.Downloading && !u.Checking;
        string? what = !offered || u.Downloading ? null
            : u.MsiInstall == false ? L.T("便携版：「更新」会打开发布页，请下载新的 zip 解压后替换。")
            : L.T("「更新」会下载安装包、用 GitHub 提供的 SHA-256 校验，然后退出 HomePodCast 安装（Windows 会请求管理员权限），装好后自动重新启动。");
        Install.AccessibleDescription = what;
        _tips.SetToolTip(Install, what);
    }
}
