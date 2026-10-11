using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using HomePodCast.Protocol;

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

/// <summary>TXT record data: length-prefixed "key=value" strings (mDNS answers; /info's "txtAirPlay" is the same bytes).</summary>
public static class TxtRecord
{
    public static Dictionary<string, string> Parse(byte[] data) => Parse(data, 0, data.Length);

    /// <summary>The entries in <paramref name="data"/>[start..start+length]; stops at an entry that runs past the end.</summary>
    public static Dictionary<string, string> Parse(byte[] data, int start, int length)
    {
        var txt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int end = Math.Min(data.Length, start + length);
        for (int p = start; p < end;)
        {
            int l = data[p++];
            if (p + l > end) break;
            var kv = Encoding.UTF8.GetString(data, p, l);
            p += l;
            int eq = kv.IndexOf('=');
            if (eq > 0) txt[kv[..eq]] = kv[(eq + 1)..];
        }
        return txt;
    }

    /// <summary>The TXT record a speaker's /info carries ("txtAirPlay"); null when it has none.</summary>
    public static Dictionary<string, string>? FromInfo(IReadOnlyDictionary<string, object?>? info) =>
        info?.GetValueOrDefault("txtAirPlay") is byte[] bytes ? Parse(bytes) : null;
}

/// <summary>Model identifiers in plain words ("AudioAccessory6,1" → "HomePod 第二代").</summary>
public static class SpeakerModels
{
    public static string Name(string model) => model switch
    {
        _ when model.StartsWith("AudioAccessory6") => L.T("HomePod 第二代"),
        _ when model.StartsWith("AudioAccessory5") => "HomePod mini",
        _ when model.StartsWith("AudioAccessory1") => "HomePod",
        _ when model.StartsWith("AppleTV") => "Apple TV",
        _ => model,
    };
}

/// <summary>
/// What 设置 → 音箱信息 shows about one speaker, from its /info (fresh) and the TXT record of the last scan (a fallback:
/// /info carries the TXT record too). Every field may be missing.
/// </summary>
public sealed record SpeakerDetails(
    string Name,
    string? Model,
    string? SystemVersion,
    string? AirPlayVersion,
    string? Address,
    string? WifiMac,
    string? BluetoothAddress,
    string? DeviceId,
    long? StatusFlags,
    long? TxtStatusFlags)
{
    public bool? SessionActive => StatusFlags is { } f ? Net.StatusFlags.SessionActive(f) : null;

    /// <summary>"HomePod 第二代（AudioAccessory6,1）"; an unknown model as it is.</summary>
    public string? ModelText => Model is { } m ? ModelDescription(m) : null;

    public static string ModelDescription(string model)
    {
        string name = SpeakerModels.Name(model);
        return name == model ? model : L.F("{0}（{1}）", name, model);
    }

    /// <summary>"27.0 (24J361)": the OS version (TXT osvers) and its build (/info osBuildVersion), or whichever is known.</summary>
    public static string? SystemVersionText(string? version, string? build)
    {
        version = Clean(version);
        build = Clean(build);
        return (version, build) switch
        {
            (null, null) => null,
            ({ } v, null) => v,
            (null, { } b) => b,
            var (v, b) => $"{v} ({b})",
        };
    }

    public static SpeakerDetails Build(string name, IPAddress? address, IReadOnlyDictionary<string, string>? scanTxt,
        IReadOnlyDictionary<string, object?>? info)
    {
        var infoTxt = TxtRecord.FromInfo(info);
        string? Txt(string key) => Clean(infoTxt?.GetValueOrDefault(key)) ?? Clean(scanTxt?.GetValueOrDefault(key));
        string? Info(string key) => Clean(info?.GetValueOrDefault(key) as string);
        bool known = address != null && !address.Equals(IPAddress.None) && !address.Equals(IPAddress.Any);
        return new SpeakerDetails(
            Info("name") ?? name,
            Info("model") ?? Txt("model"),
            SystemVersionText(Txt("osvers"), Info("osBuildVersion")),
            Txt("srcvers") ?? Info("sourceVersion"),
            known ? address!.ToString() : null,
            Info("macAddress")?.ToUpperInvariant(),
            Txt("btaddr")?.ToUpperInvariant(),
            (Txt("deviceid") ?? Info("deviceID"))?.ToUpperInvariant(),
            Net.StatusFlags.FromInfo(info) ?? Net.StatusFlags.FromTxt(infoTxt) ?? Net.StatusFlags.FromTxt(scanTxt),
            Net.StatusFlags.FromTxt(infoTxt) ?? Net.StatusFlags.FromTxt(scanTxt));
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>GET /info on a connection of its own: no pairing, no session, nothing changed on the speaker.</summary>
public static class SpeakerInfoReader
{
    public static async Task<IReadOnlyDictionary<string, object?>> ReadAsync(IPAddress address, CancellationToken ct)
    {
        using var rtsp = await RtspConnection.ConnectAsync(address, 7000, ct);
        using var close = ct.Register(rtsp.Dispose); // the request below blocks on the socket: closing it ends the wait
        var reply = await Task.Run(() => rtsp.Rtsp("GET", "/info"), CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (!reply.IsSuccess || reply.Body.Length == 0) throw new AirPlayException($"GET /info: {reply.StartLine}");
        return BPlist.ReadDict(reply.Body);
    }
}

public enum SpeakerInfoState { Reading, Ready, Failed }

/// <param name="Info">The last /info read (kept while a new read runs, and after a failed one).</param>
/// <param name="ReadUtc">When <paramref name="Info"/> was read.</param>
/// <param name="Error">Why the last read failed.</param>
public sealed record SpeakerInfoEntry(SpeakerInfoState State, IReadOnlyDictionary<string, object?>? Info, DateTime? ReadUtc, string? Error);

/// <summary>
/// The last /info of every speaker, by device id, read off the UI thread with a short timeout: for 设置 → 音箱信息 and
/// for the takeover check before connecting (<see cref="InfoBusyReader"/>). One read per speaker at a time; a second
/// request while one runs gets the same result.
/// </summary>
public sealed class SpeakerInfoCache(Func<IPAddress, CancellationToken, Task<IReadOnlyDictionary<string, object?>>> read, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly object _lock = new();
    private readonly Dictionary<string, SpeakerInfoEntry> _entries = new();
    private readonly Dictionary<string, Task<SpeakerInfoEntry>> _reading = new();

    public TimeSpan Timeout { get; } = timeout ?? DefaultTimeout;

    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>A speaker's entry changed (reading, read, failed); raised on the thread pool with its device id key.</summary>
    public event Action<string>? Changed;

    public static string Key(string deviceId) => StreamController.Normalize(deviceId).ToUpperInvariant();

    public SpeakerInfoEntry? Get(string deviceId)
    {
        lock (_lock) return _entries.GetValueOrDefault(Key(deviceId));
    }

    /// <param name="label">The speaker's name for the log.</param>
    /// <param name="log">One log line for this read (the takeover check's repeated reads log only what they decide).</param>
    public Task<SpeakerInfoEntry> RefreshAsync(string deviceId, string label, IPAddress? address, bool log = true)
    {
        string key = Key(deviceId);
        Task<SpeakerInfoEntry> task;
        lock (_lock)
        {
            if (_reading.TryGetValue(key, out var running)) return running;
            var previous = _entries.GetValueOrDefault(key);
            _entries[key] = new SpeakerInfoEntry(SpeakerInfoState.Reading, previous?.Info, previous?.ReadUtc, null);
            task = Task.Run(() => ReadAsync(key, label, address, previous, log));
            _reading[key] = task;
        }
        Changed?.Invoke(key);
        return task;
    }

    private async Task<SpeakerInfoEntry> ReadAsync(string key, string label, IPAddress? address, SpeakerInfoEntry? previous, bool log)
    {
        var sw = Stopwatch.StartNew();
        SpeakerInfoEntry entry;
        try
        {
            if (address == null || address.Equals(IPAddress.None) || address.Equals(IPAddress.Any))
                throw new AirPlayException("address unknown");
            using var cts = new CancellationTokenSource(Timeout);
            var reading = read(address, cts.Token);
            _ = reading.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // given up on below
            var info = await reading.WaitAsync(Timeout);
            entry = new SpeakerInfoEntry(SpeakerInfoState.Ready, info, UtcNow(), null);
            if (log) Log.Info($"speaker info: {Log.Name(label)} {address} read in {sw.ElapsedMilliseconds} ms, {DescribeFlags(info)}");
        }
        catch (Exception ex)
        {
            string error = ex is TimeoutException or OperationCanceledException
                ? $"no answer in {Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s"
                : ex.Message;
            entry = new SpeakerInfoEntry(SpeakerInfoState.Failed, previous?.Info, previous?.ReadUtc, error);
            if (log) Log.Info($"speaker info: {Log.Name(label)} {address?.ToString() ?? "-"} unreadable ({error})");
        }
        lock (_lock)
        {
            _entries[key] = entry;
            _reading.Remove(key);
        }
        Changed?.Invoke(key);
        return entry;
    }

    /// <summary>"statusFlags 0x80404 (bit 17 sessionActive=False), TXT flags agree", or how the TXT inside /info differs.</summary>
    public static string DescribeFlags(IReadOnlyDictionary<string, object?> info)
    {
        var status = StatusFlags.FromInfo(info);
        var txt = StatusFlags.FromTxt(TxtRecord.FromInfo(info));
        string text = status is { } s ? "statusFlags " + StatusFlags.Describe(s) : "no statusFlags";
        return text + (txt is not { } t ? ", no TXT flags" : t == status ? ", TXT flags agree" : $", TXT flags {StatusFlags.Describe(t)}");
    }
}
