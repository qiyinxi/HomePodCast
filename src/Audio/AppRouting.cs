using System.Runtime.InteropServices;

namespace HomePodCast.Audio;

/// <summary>
/// What the capture takes, persisted in <see cref="AppConfig"/>: the output device (<see cref="CaptureDeviceId"/>)
/// and the per-app destinations on it (HomePod / this PC / both). Changed on the UI thread; the capture reads
/// <see cref="Rules"/> (an immutable snapshot) and <see cref="CaptureDeviceId"/> from its own threads.
/// </summary>
public sealed class AppRouting
{
    private readonly AppConfig _config;
    private readonly Action _save;
    private volatile RouteRules _rules;
    private volatile string? _captureDeviceId;

    public AppRouting(AppConfig config, Action? save = null)
    {
        _config = config;
        _save = save ?? config.Save;
        _rules = Build();
        _captureDeviceId = Normalize(config.CaptureDeviceId);
    }

    /// <summary>The output device to capture; null = follow the Windows default output (see <see cref="CaptureEndpoint"/>).</summary>
    public string? CaptureDeviceId => _captureDeviceId;

    /// <summary>Choose the captured output device (null = the Windows default output); saved, raises <see cref="Changed"/>.</summary>
    public void SetCaptureDevice(string? id, string? name)
    {
        id = Normalize(id);
        if (id == null ? _captureDeviceId == null : CaptureEndpoint.Same(id, _captureDeviceId)) return;
        _config.CaptureDeviceId = id;
        _config.CaptureDeviceName = id == null ? null : name;
        _captureDeviceId = id;
        try { _save(); } catch (Exception ex) { Log.Warn($"routing: config not saved: {ex.Message}"); }
        Log.Info(id == null ? "capture device: follow the Windows default output" : $"capture device: \"{name}\" {id}");
        Changed?.Invoke();
    }

    private static string? Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    /// <summary>Process loopback needs Windows 10 2004 (build 19041); older systems keep today's capture.</summary>
    public static bool Supported => ProcessLoopback.IsSupported;

    public RouteRules Rules => _rules;

    /// <summary>Whether the capture splits apps (process loopback) instead of taking the whole endpoint.</summary>
    public bool Active => Supported && _rules.NeedsRouting;

    /// <summary>Raised (on the caller's thread) after any rule changed.</summary>
    public event Action? Changed;

    public AudioRoute Default
    {
        get => _rules.Default;
        set
        {
            if (_config.RouteDefault == value) return;
            _config.RouteDefault = value;
            Commit();
        }
    }

    /// <summary>The app's own rule, or null when it follows <see cref="Default"/>.</summary>
    public AudioRoute? Get(string key) => _rules.Apps.TryGetValue(RouteRules.KeyFor(key), out var r) ? r : null;

    public void Set(string key, AudioRoute? route)
    {
        key = RouteRules.KeyFor(key);
        if (key.Length == 0) return;
        if (route is { } r)
        {
            if (_config.AppRoutes.TryGetValue(key, out var old) && old == r) return;
            _config.AppRoutes[key] = r;
        }
        else if (!_config.AppRoutes.Remove(key)) return;
        Commit();
    }

    private void Commit()
    {
        _rules = Build();
        try { _save(); } catch (Exception ex) { Log.Warn($"routing: config not saved: {ex.Message}"); }
        Log.Info($"routing: default={_rules.Default} rules={string.Join(",", _rules.Apps.Select(kv => $"{kv.Key}={kv.Value}"))} " +
                 $"active={Active}");
        Changed?.Invoke();
    }

    private RouteRules Build()
    {
        var apps = new Dictionary<string, AudioRoute>();
        foreach (var (key, route) in _config.AppRoutes)
            if (RouteRules.KeyFor(key ?? "") is { Length: > 0 } k && Enum.IsDefined(route)) apps[k] = route;
        return new RouteRules(_config.RouteDefault, apps);
    }

    /// <summary>Whether the default output is muted or at 0 % (nothing is heard on this PC).</summary>
    public static bool LocalOutputSilent()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        IMMDevice? device = null;
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device) < 0) return false;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var obj) < 0) return false;
            var volume = (IAudioEndpointVolume)obj;
            try
            {
                return volume.GetMute(out bool muted) >= 0 && muted ||
                       volume.GetMasterVolumeLevelScalar(out float level) >= 0 && level <= 0.001f;
            }
            finally
            {
                Marshal.ReleaseComObject(volume);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            if (device != null) Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
        }
    }
}

/// <summary>
/// Extra inputs (see <see cref="IMixSource"/>) mixed into whatever capture is running. Thread-safe;
/// the capture thread takes a snapshot every cycle.
/// </summary>
public sealed class MixSources
{
    private volatile IMixSource[] _items = [];
    private readonly object _lock = new();

    public IReadOnlyList<IMixSource> Snapshot => _items;

    public void Add(IMixSource source)
    {
        lock (_lock) _items = [.. _items, source];
    }

    public void Remove(IMixSource source)
    {
        lock (_lock) _items = _items.Where(s => !ReferenceEquals(s, source)).ToArray();
    }
}
