using System.Security.Cryptography;
using System.Text.Json;
using HomePodCast.Protocol;

namespace HomePodCast.Net;

/// <summary>A stored pairing as the UI lists it (no keys).</summary>
public sealed record PairedDevice(string DeviceId, string Name, DateTime PairedUtc);

/// <summary>
/// The HAP pairings with receivers that need one (Apple TV), by device id: pairings.dat next to config.json, the whole
/// file a DPAPI blob for the current Windows user with HomePodCast's own entropy (like the VLC password), so only this
/// user on this PC can read it and config.json never holds a key. Read on demand (once per connect; no file, as long as
/// nothing was ever paired), never cached, logged or shown. Thread-safe.
/// </summary>
public sealed class PairingStore(string path)
{
    public const string FileName = "pairings.dat";

    private static ReadOnlySpan<byte> Entropy => "HomePodCast/AirPlay HAP pairings/v1"u8;

    private readonly object _lock = new();

    public string FilePath { get; } = path;

    /// <summary>The app's store, in <see cref="AppConfig.Directory"/>.</summary>
    public static PairingStore Default => new(Path.Combine(AppConfig.Directory, FileName));

    /// <summary>A pairing was saved or forgotten (raised on the caller's thread, outside the lock).</summary>
    public event Action? Changed;

    /// <summary>"3E:B7:8E:9F:64:72", "3eb78e9f6472" and "3E-B7-…" are the same receiver.</summary>
    internal static string Key(string deviceId) => deviceId.Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();

    public HapPairing? Find(string deviceId)
    {
        string key = Key(deviceId);
        lock (_lock) return Load().FirstOrDefault(e => e.DeviceId == key)?.ToPairing();
    }

    public bool Contains(string deviceId) => Find(deviceId) != null;

    /// <summary>The paired receivers, most recent first.</summary>
    public IReadOnlyList<PairedDevice> List()
    {
        lock (_lock)
            return Load().OrderByDescending(e => e.PairedUtc).Select(e => new PairedDevice(e.DeviceId, e.Name, e.PairedUtc)).ToList();
    }

    /// <summary>Stores (or replaces) the pairing with <paramref name="deviceId"/>; throws when the file can't be written.</summary>
    public void Save(string deviceId, string name, HapPairing pairing)
    {
        string key = Key(deviceId);
        lock (_lock)
        {
            var entries = Load();
            entries.RemoveAll(e => e.DeviceId == key);
            entries.Add(new Entry
            {
                DeviceId = key,
                Name = name,
                PairedUtc = DateTime.UtcNow,
                ControllerId = pairing.ControllerId,
                ControllerKey = pairing.ControllerSeed,
                AccessoryId = pairing.AccessoryId,
                AccessoryKey = pairing.AccessoryPublicKey,
            });
            Write(entries);
        }
        Log.Info($"pairing saved for {Log.Name(name)}");
        Changed?.Invoke();
    }

    /// <summary>Removes the pairing with <paramref name="deviceId"/>; false when there was none.</summary>
    public bool Forget(string deviceId)
    {
        string key = Key(deviceId);
        lock (_lock)
        {
            var entries = Load();
            if (entries.RemoveAll(e => e.DeviceId == key) == 0) return false;
            Write(entries);
        }
        Log.Info("pairing forgotten");
        Changed?.Invoke();
        return true;
    }

    private List<Entry> Load()
    {
        byte[] blob;
        try
        {
            if (!File.Exists(FilePath)) return [];
            blob = File.ReadAllBytes(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"pairings: unreadable ({ex.Message})");
            return [];
        }
        var plain = Dpapi.Unprotect(blob, Entropy);
        if (plain == null)
        {
            Log.Warn("pairings: the file cannot be decrypted (another Windows user or PC, or damaged); ignoring it");
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<StoreFile>(plain)?.Pairings?.Where(e => e.IsComplete).ToList() ?? [];
        }
        catch (JsonException)
        {
            Log.Warn("pairings: the file is damaged; ignoring it");
            return [];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private void Write(List<Entry> entries)
    {
        if (entries.Count == 0)
        {
            File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(new StoreFile { Pairings = entries });
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllBytes(tmp, Dpapi.Protect(plain, Entropy));
            File.Move(tmp, FilePath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    internal sealed class StoreFile
    {
        public int Version { get; set; } = 1;
        public List<Entry>? Pairings { get; set; }
    }

    internal sealed class Entry
    {
        public string DeviceId { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime PairedUtc { get; set; }
        public string ControllerId { get; set; } = "";
        public byte[] ControllerKey { get; set; } = [];
        public byte[] AccessoryId { get; set; } = [];
        public byte[] AccessoryKey { get; set; } = [];

        public bool IsComplete => DeviceId.Length > 0 && ControllerId.Length > 0 && ControllerKey.Length == Ed25519.SeedSize &&
                                  AccessoryId.Length > 0 && AccessoryKey.Length == Ed25519.PublicKeySize;

        public HapPairing ToPairing() => new(ControllerId, ControllerKey, AccessoryId, AccessoryKey);
    }
}
