namespace HomePodCast;

public enum HotkeyAction { VolumeUp, VolumeDown, Mute, NextScene }

/// <summary>A global key combination such as Ctrl+Alt+PageUp, in the text form stored in the config.</summary>
public readonly record struct Hotkey(Keys Modifiers, Keys Key)
{
    // Defaults: Ctrl+Alt with keys games and common apps leave alone. Not Ctrl+Alt+arrows (Intel
    // graphics rotates the screen, music players use them), not End/Home/Break (Remote Desktop), and
    // volume/mute on the navigation cluster, which never doubles as an AltGr character. Ctrl+Alt+M was
    // the first choice for mute but another program already held it on the development machine.
    public static readonly IReadOnlyDictionary<HotkeyAction, string> Defaults = new Dictionary<HotkeyAction, string>
    {
        [HotkeyAction.VolumeUp] = "Ctrl+Alt+PageUp",
        [HotkeyAction.VolumeDown] = "Ctrl+Alt+PageDown",
        [HotkeyAction.Mute] = "Ctrl+Alt+Insert",
        [HotkeyAction.NextScene] = "Ctrl+Alt+N",
    };

    /// <summary>Needs Ctrl or Alt, so plain typing and Shift+letter are never taken from other programs.</summary>
    public bool IsValid => Key != Keys.None && !IsModifierKey(Key) && (Modifiers & (Keys.Control | Keys.Alt)) != 0;

    public static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey or Keys.RControlKey
        or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey or Keys.Menu or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    private static readonly (Keys Key, string Name)[] Names =
    [
        (Keys.PageUp, "PageUp"), (Keys.PageDown, "PageDown"), (Keys.Insert, "Insert"), (Keys.Delete, "Delete"),
        (Keys.Home, "Home"), (Keys.End, "End"), (Keys.Up, "Up"), (Keys.Down, "Down"), (Keys.Left, "Left"),
        (Keys.Right, "Right"), (Keys.Space, "Space"), (Keys.Pause, "Pause"), (Keys.Back, "Backspace"),
        (Keys.OemMinus, "-"), (Keys.Oemplus, "="), (Keys.OemOpenBrackets, "["), (Keys.OemCloseBrackets, "]"),
        (Keys.OemSemicolon, ";"), (Keys.OemQuotes, "'"), (Keys.Oemcomma, ","), (Keys.OemPeriod, "."),
        (Keys.OemQuestion, "/"), (Keys.OemPipe, "\\"), (Keys.Oemtilde, "`"),
    ];

    public override string ToString()
    {
        if (Key == Keys.None) return "";
        var parts = new List<string>(4);
        if ((Modifiers & Keys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & Keys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & Keys.Shift) != 0) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Keys key)
    {
        foreach (var (k, name) in Names) if (k == key) return name;
        if (key is >= Keys.D0 and <= Keys.D9) return ((char)('0' + (key - Keys.D0))).ToString();
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9) return "Num" + (key - Keys.NumPad0);
        return key.ToString();
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        Keys mods = Keys.None;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Keys.Control; break;
                case "alt": mods |= Keys.Alt; break;
                case "shift": mods |= Keys.Shift; break;
                default: return false;
            }
        }
        var last = parts[^1];
        Keys key = Keys.None;
        foreach (var (k, name) in Names)
            if (string.Equals(name, last, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        if (key == Keys.None)
        {
            if (last.Length == 1 && char.IsAsciiDigit(last[0])) key = Keys.D0 + (last[0] - '0');
            else if (last.Length == 4 && last.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(last[3]))
                key = Keys.NumPad0 + (last[3] - '0');
            else if (!Enum.TryParse(last, ignoreCase: true, out key) || int.TryParse(last, out _)) return false;
        }
        hotkey = new Hotkey(mods, key & Keys.KeyCode);
        return hotkey.IsValid;
    }

    /// <summary>Configured text for an action: missing means the default, "" means switched off.</summary>
    public static string ConfigText(AppConfig cfg, HotkeyAction action) =>
        cfg.Hotkeys?.TryGetValue(action, out var s) == true ? s ?? "" : Defaults[action];

    /// <summary>The combination configured for an action, or null if it is off or unreadable.</summary>
    public static Hotkey? FromConfig(AppConfig cfg, HotkeyAction action) =>
        TryParse(ConfigText(cfg, action), out var hk) ? hk : null;
}
