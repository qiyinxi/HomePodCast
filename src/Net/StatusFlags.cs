using System.Globalization;

namespace HomePodCast.Net;

/// <summary>
/// A speaker's status flags, as its TXT record ("flags=0x1a0404") and its GET /info ("statusFlags", the same number in
/// decimal) carry them; bits numbered as in https://openairplay.github.io/airplay-spec/status_flags.html. Read on a
/// HomePod (2nd generation) on 2026-10-11: TXT and /info agreed, 0x1A0404 while a sender streamed to it and 0x80404 idle
/// (bit 17, and with it bit 20, cleared).
/// </summary>
public static class StatusFlags
{
    /// <summary>Bit 17, ReceiverSessionIsActive: some sender has a session with the speaker right now.</summary>
    public const long ReceiverSessionIsActive = 1L << 17;

    /// <summary>
    /// Bit 20: the speaker's playback timeline is running; on 2026-10-28 it came with an anchored buffered stream, and it is
    /// set while a realtime sender streams too (0x1A0404 above). It also appears for data the speaker can't decode, so it
    /// confirms the anchor, not the audio. Only `stream --surround` logs it.
    /// </summary>
    public const long AnchoredTimelineRunning = 1L << 20;

    /// <summary>A TXT "flags" value: hex, with or without "0x". Null when missing or unreadable.</summary>
    public static long? Parse(string? txtValue)
    {
        var s = txtValue?.Trim() ?? "";
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return s.Length > 0 && long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    public static long? FromTxt(IReadOnlyDictionary<string, string>? txt) => Parse(txt?.GetValueOrDefault("flags"));

    /// <summary>/info's "statusFlags" (an integer).</summary>
    public static long? FromInfo(IReadOnlyDictionary<string, object?>? info) =>
        info?.GetValueOrDefault("statusFlags") is long flags ? flags : null;

    public static bool SessionActive(long flags) => (flags & ReceiverSessionIsActive) != 0;

    public static string Hex(long flags) => "0x" + flags.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>"0x80404 (bit 17 sessionActive=False)", for the `info` command and the log.</summary>
    public static string Describe(long flags) => $"{Hex(flags)} (bit 17 sessionActive={SessionActive(flags)})";
}
