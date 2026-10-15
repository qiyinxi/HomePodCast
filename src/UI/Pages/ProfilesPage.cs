using HomePodCast.UI.Controls;

namespace HomePodCast.UI.Pages;

internal sealed class ProfilesPage : ScrollPage
{
    private readonly ProfilesPanel _profiles;
    public ProfilesPage(TrayApp app) : base(L.T("使用配置"))
    {
        _profiles = new ProfilesPanel(app);
        Content.Controls.Add(_profiles);
    }
    public void Reload() => _profiles.Reload();
    public override void PageShown() => Reload();
}
