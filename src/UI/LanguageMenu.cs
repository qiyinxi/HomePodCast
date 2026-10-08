namespace HomePodCast.UI;

/// <summary>
/// The UI language (config "Language"), chosen on the 设置 page. The window is built once with its texts, so a
/// change is applied by restarting the app (Program.RunGui starts the new copy once this one has exited).
/// </summary>
internal static class LanguageMenu
{
    public static bool RestartRequested { get; private set; }

    /// <summary>Every supported language (L.Languages) under its own name, sorted by that name.</summary>
    internal static readonly (string Value, string Name)[] Choices = L.Languages
        .Select(lang => (Value: lang, Name: L.NativeName(lang)))
        .OrderBy(c => c.Name, StringComparer.InvariantCultureIgnoreCase)
        .ThenBy(c => c.Value, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The "auto" entry, naming the language it picks now: "Use Windows setting (Svenska)".</summary>
    internal static string AutoName =>
        L.F("{0}（{1}）", L.T("跟随 Windows"), L.NativeName(L.Resolve(L.Auto, System.Globalization.CultureInfo.CurrentUICulture)));

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
