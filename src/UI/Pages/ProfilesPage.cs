using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal sealed class ProfilesPage : ScrollPage
{
    private readonly ProfilesPanel _profiles;
    public ProfilesPage(TrayApp app) : this(new ProfilesPanel(app)) { }
    internal ProfilesPage(AppConfig config, Action<string?, string> save, Func<string, IWin32Window?, bool> apply,
        Action<string> delete) : this(new ProfilesPanel(config, save, apply, delete)) { }
    private ProfilesPage(ProfilesPanel profiles) : base(L.T("使用配置"))
    {
        _profiles = profiles;
        Content.Controls.Add(_profiles);
    }
    public void Reload()
    {
        using var layout = LayoutPanel.BatchLayout(this);
        _profiles.Reload();
    }
    public override void PageShown() => Reload();
}
