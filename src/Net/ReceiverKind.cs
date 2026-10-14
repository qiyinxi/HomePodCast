using System.Globalization;

namespace HomePodCast.Net;

public enum ReceiverKind { HomePod, AppleTv, AppleAirPlay1, ThirdPartyAirPlay2, ThirdPartyAirPlay1, Unknown }

/// <summary>Advertised capabilities, not a promise that a receiver accepts a particular stream.</summary>
public static class ReceiverKinds
{
    // https://openairplay.github.io/airplay-spec/features.html and pyatv/protocols/airplay/utils.py,
    // checked 2026-10-14: the names for bits 38/48 differ, but both indicate newer pairing/control.
    // Classification uses the numeric capabilities only, never assumes a pairing method from them.
    public const ulong CoreUtilsPairing = 1UL << 38;
    public const ulong BufferedAudio = 1UL << 40;
    public const ulong Ptp = 1UL << 41;
    public const ulong HomeKitPairing = 1UL << 46;
    public const ulong TransientPairing = 1UL << 48;

    public static ulong Features(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var words = value.Split(',');
        static bool Word(string s, out ulong n)
        {
            s = s.Trim();
            bool hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ulong.TryParse(hex ? s[2..] : s, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out n);
        }
        if (words.Length == 1) return Word(words[0], out var n) ? n : 0;
        return words.Length == 2 && Word(words[0], out var low) && Word(words[1], out var high)
            && low <= uint.MaxValue && high <= uint.MaxValue ? low | (high << 32) : 0;
    }

    public static ReceiverKind Classify(string model, IReadOnlyDictionary<string, string>? txt,
        bool airPlay = true, bool raop = false, IReadOnlyDictionary<string, object?>? info = null)
    {
        string Text(string key) => info?.GetValueOrDefault(key)?.ToString() ?? txt?.GetValueOrDefault(key) ?? "";
        model = Text("model") is { Length: > 0 } m ? m : model;
        if (model.Length == 0) model = Text("am");
        string manufacturer = Text("manufacturer");
        bool apple = manufacturer.Length == 0 || manufacturer.Equals("Apple", StringComparison.OrdinalIgnoreCase)
            || manufacturer.Equals("Apple Inc.", StringComparison.OrdinalIgnoreCase);
        if (apple && model.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.HomePod;
        if (apple && model.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(model[7..].Split(',')[0], out var generation) && generation >= 5) return ReceiverKind.AppleTv;
        ulong features = Features(Text("features") is { Length: > 0 } f ? f : Text("ft"));
        bool modern = (features & (CoreUtilsPairing | BufferedAudio | Ptp | HomeKitPairing | TransientPairing)) != 0
            || (int.TryParse((Text("srcvers") is { Length: > 0 } v ? v : Text("sourceVersion")).Split('.')[0], out var major) && major >= 300);
        if (airPlay && modern) return ReceiverKind.ThirdPartyAirPlay2;
        if (apple && model.StartsWith("AirPort", StringComparison.OrdinalIgnoreCase)) return ReceiverKind.AppleAirPlay1;
        bool oldTv = model.StartsWith("AppleTV2,", StringComparison.OrdinalIgnoreCase)
            || model.StartsWith("AppleTV3,", StringComparison.OrdinalIgnoreCase);
        // Older Apple TV identifiers are also used by projector emulators. Without manufacturer evidence,
        // keep them on the compatibility path; never infer modern PIN pairing from that model alone.
        if (oldTv && apple && manufacturer.Length > 0) return ReceiverKind.AppleAirPlay1;
        if (raop || oldTv || (features & (1UL << 30)) != 0 || (airPlay && (features & (1UL << 9)) != 0))
            return ReceiverKind.ThirdPartyAirPlay1;
        return ReceiverKind.Unknown;
    }

    public static string Label(ReceiverKind kind) => kind switch
    {
        ReceiverKind.HomePod => "",
        ReceiverKind.AppleTv => L.T("Apple TV（实验性）"), // experimental since 1.1
        ReceiverKind.AppleAirPlay1 or ReceiverKind.ThirdPartyAirPlay1 => L.T("AirPlay 1 · 兼容模式（实验性）"), // experimental since 1.1
        ReceiverKind.ThirdPartyAirPlay2 => L.T("AirPlay 2 · 第三方（实验性，未在实机上测试）"), // experimental since 1.1
        _ => L.T("未知 AirPlay 设备（实验性）"), // experimental since 1.1
    };

    public static int SortOrder(ReceiverKind kind) => kind is ReceiverKind.HomePod or ReceiverKind.AppleTv ? 0 : 1;
}
