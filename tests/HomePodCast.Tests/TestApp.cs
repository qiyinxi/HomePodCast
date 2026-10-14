using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;

namespace HomePodCast.Tests;

/// <summary>
/// A <see cref="TrayApp"/> whose constructor never ran, for building the real pages and <see cref="MainWindow"/> in a test:
/// no tray icon, no discovery, no connection, no local API, no device notifications. Only the members the pages read are
/// set here; when a page starts reading another one, set it here too. The config lives in memory (what a page saves goes
/// to the unit-tests profile, TestLanguage), and the speaker info is "read" at once from <see cref="SpeakerInfo"/>.
/// </summary>
internal static class TestApp
{
    public const string DeviceId = "AA:BB:CC:DD:EE:FF";

    /// <summary>What the made-up HomePod answers to GET /info.</summary>
    public static readonly IReadOnlyDictionary<string, object?> SpeakerInfo = new Dictionary<string, object?>
    {
        ["model"] = "AudioAccessory6,1", ["osBuildVersion"] = "22J580", ["sourceVersion"] = "870.14.1",
        ["macAddress"] = "AA:BB:CC:DD:EE:01", ["deviceID"] = DeviceId, ["statusFlags"] = 4L,
    };

    /// <summary>A config with a selected HomePod (so 设置 shows its 音箱信息) that never connects.</summary>
    public static AppConfig Config() => new()
    {
        DeviceId = DeviceId,
        DeviceName = "卧室",
        Host = "192.0.2.10", // TEST-NET-1: nothing answers there
        AutoConnect = false,
        Effects = new EffectsSettings(),
    };

    public static TrayApp Create(AppConfig config)
    {
        var app = (TrayApp)RuntimeHelpers.GetUninitializedObject(typeof(TrayApp));
        Set(app, "<Config>k__BackingField", config);
        Set(app, "<Controller>k__BackingField", new StreamController(config.FifoTargetMs));
        Set(app, "<Routing>k__BackingField", new AppRouting(config, () => { }));
        Set(app, "<MixSources>k__BackingField", new MixSources());
        Set(app, "<UnavailableHotkeys>k__BackingField", new HashSet<HotkeyAction>());
        List<AirPlayDevice> scan = config.DeviceId is { } id
            ? [new(config.DeviceName ?? "?", id, IPAddress.Parse("192.0.2.10"), 7000, "AudioAccessory6,1",
                new Dictionary<string, string> { ["model"] = "AudioAccessory6,1", ["deviceid"] = id })]
            : [];
        Set(app, "<LastScan>k__BackingField", scan);
        Set(app, "<SpeakerInfo>k__BackingField", new SpeakerInfoCache((_, _) => Task.FromResult(SpeakerInfo)));
        Set(app, "<Pairings>k__BackingField",
            new PairingStore(Path.Combine(Path.GetTempPath(), $"hpc-test-{Guid.NewGuid():N}", PairingStore.FileName)));
        Set(app, "_fx", new MicEffects(config.Effects ??= new EffectsSettings()));
        Set(app, "_updateStatus", "上次检查：2026-10-15 09:30");
        Set(app, "_ui", SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
        Set(app, "_soundActivity", new SoundActivity());
        return app;
    }

    private static void Set(TrayApp app, string field, object? value) =>
        (typeof(TrayApp).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
         ?? throw new InvalidOperationException($"TrayApp has no field {field} any more: update TestApp")).SetValue(app, value);

    /// <summary>A private field of a page or window (its controls).</summary>
    public static T Field<T>(object owner, string name) =>
        (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException($"{owner.GetType().Name} has no field {name}"));
}
