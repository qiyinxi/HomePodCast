namespace HomePodCast.UI;

/// <summary>
/// The UI language (config "Language"): a tray submenu, and the choice on the 设置 page. The window is built
/// once with its texts, so a change is applied by restarting the app (Program.RunGui starts the new copy once
/// this one has exited).
/// </summary>
internal static class LanguageMenu
{
    public static bool RestartRequested { get; private set; }

    // Language names are shown in their own language, never translated.
    internal static readonly (string Value, string Name)[] Choices =
    [
        ("zh-CN", "简体中文"),
        ("zh-TW", "繁體中文"),
        ("en", "English"),
        ("ja", "日本語"),
    ];

    public static ToolStripMenuItem Create(AppConfig config, Action quit)
    {
        // Keep "Language" recognizable for someone who landed in a language they don't read.
        var root = new ToolStripMenuItem(L.Language == "en" ? L.T("语言") : L.T("语言") + " / Language");
        var auto = new ToolStripMenuItem(L.T("自动（跟随系统）")) { Tag = L.Auto };
        root.DropDownItems.Add(auto);
        root.DropDownItems.Add(new ToolStripSeparator());
        foreach (var (value, name) in Choices)
            root.DropDownItems.Add(new ToolStripMenuItem(name) { Tag = value });

        root.DropDownOpening += (_, _) =>
        {
            var current = Normalize(config.Language);
            foreach (var item in root.DropDownItems.OfType<ToolStripMenuItem>())
                item.Checked = (string)item.Tag! == current;
        };
        foreach (var item in root.DropDownItems.OfType<ToolStripMenuItem>())
            item.Click += (_, _) => Choose(config, (string)item.Tag!, quit);
        return root;
    }

    internal static string Normalize(string? configured) =>
        Choices.FirstOrDefault(c => string.Equals(c.Value, configured, StringComparison.OrdinalIgnoreCase)).Value ?? L.Auto;

    internal static void Choose(AppConfig config, string value, Action quit)
    {
        if (Normalize(config.Language) == value) return;
        config.Language = value;
        config.Save();
        var effective = L.Resolve(value, System.Globalization.CultureInfo.CurrentUICulture);
        Log.Info($"UI language set to {value} ({effective})");
        if (effective == L.Language) return; // e.g. "auto" picked while already showing the system language

        var answer = MessageBox.Show(
            L.T("界面语言会在 HomePodCast 重新启动后切换。现在重新启动吗？\n\n重新启动时声音会中断几秒。"),
            "HomePodCast", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;
        RestartRequested = true;
        quit();
    }
}
