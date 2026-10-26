using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HomePodCast.Audio;
using HomePodCast.Net;

namespace HomePodCast;

[JsonConverter(typeof(JsonStringEnumConverter<MixerOutputKind>))]
public enum MixerOutputKind { Current, Speaker, Group, Local }

public sealed record MixerOutput(MixerOutputKind Kind = MixerOutputKind.Current, string? Id = null);
public sealed record MixerRoute(MixerOutput Output, bool AlsoLocal = false)
{
    public static readonly MixerRoute Current = new(new MixerOutput());
    public static readonly MixerRoute Local = new(new MixerOutput(MixerOutputKind.Local));
}

public sealed record MixerDestinationSettings(Scene Scene, int LatencyMs, int FifoTargetMs = AppConfig.DefaultFifoTargetMs);

public sealed record MixerRoutingSettings
{
    public int Version { get; init; } = 2;
    public MixerRoute Default { get; init; } = MixerRoute.Current;
    public Dictionary<string, MixerRoute> Apps { get; init; } = [];
    public Dictionary<string, MixerDestinationSettings> DestinationSettings { get; init; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extensions { get; init; }
    public MixerRoute For(string key) => Apps.GetValueOrDefault(RouteRules.KeyFor(key), Default);
}

public enum MixerRoutingError { None, UnknownVersion, InvalidConfig, MissingDestination, NoSelection, DuplicateLocal }
public sealed record MixerRoutingRead(MixerRoutingSettings? Settings, MixerRoutingError Error)
{
    public bool Editable => Error == MixerRoutingError.None;
}

/// <summary>Pure configuration boundary. Reading, migrating and resolving never opens an audio or network device.</summary>
public static class MixerRoutes
{
    public const int MaxApps = 4096;

    public static MixerRoute FromLegacy(AudioRoute route) => route switch
    {
        AudioRoute.Local => MixerRoute.Local,
        AudioRoute.Both => MixerRoute.Current with { AlsoLocal = true },
        _ => MixerRoute.Current,
    };

    public static void Migrate(AppConfig config)
    {
        if (config.MixerRouting is not null) return;
        var settings = new MixerRoutingSettings
        {
            Default = FromLegacy(config.RouteDefault),
            Apps = (config.AppRoutes ?? []).GroupBy(p => RouteRules.KeyFor(p.Key))
                .ToDictionary(g => g.Key, g => FromLegacy(g.Last().Value)),
        };
        config.MixerRouting = JsonSerializer.SerializeToElement(settings);
    }

    public static MixerRoutingRead Read(AppConfig config)
    {
        if (config.MixerRouting is null)
        {
            var copy = new AppConfig { RouteDefault = config.RouteDefault, AppRoutes = config.AppRoutes };
            Migrate(copy);
            return Read(copy);
        }
        try
        {
            var raw = config.MixerRouting.Value;
            if (raw.ValueKind != JsonValueKind.Object || !raw.TryGetProperty("Version", out var version) ||
                !version.TryGetInt32(out int number)) return new(null, MixerRoutingError.InvalidConfig);
            if (number != 2) return new(null, MixerRoutingError.UnknownVersion);
            // Reject ambiguous keys and oversized sections as a whole; never silently drop saved rules.
            if (raw.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() > 1))
                return new(null, MixerRoutingError.InvalidConfig);
            if (raw.TryGetProperty("Apps", out var apps) && (apps.ValueKind != JsonValueKind.Object ||
                apps.EnumerateObject().GroupBy(p => RouteRules.KeyFor(p.Name)).Any(g => g.Count() > 1)))
                return new(null, MixerRoutingError.InvalidConfig);
            var s = raw.Deserialize<MixerRoutingSettings>();
            if (s == null || s.Apps == null || s.Apps.Count > MaxApps || s.DestinationSettings == null ||
                s.DestinationSettings.Count > MaxApps || !Valid(s.Default) ||
                s.Apps.Any(p => !ValidKey(p.Key) || !Valid(p.Value)) ||
                s.DestinationSettings.Any(p => !ValidKey(p.Key) || p.Value == null || !Enum.IsDefined(p.Value.Scene) ||
                    p.Value.LatencyMs is < 0 or > 30000 || p.Value.FifoTargetMs is < 1 or > 1000))
                return new(null, MixerRoutingError.InvalidConfig);
            return new(s with { Apps = s.Apps.ToDictionary(p => RouteRules.KeyFor(p.Key), p => p.Value) }, MixerRoutingError.None);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        { return new(null, MixerRoutingError.InvalidConfig); }
    }

    public static void Write(AppConfig config, MixerRoutingSettings settings)
    {
        if (!Read(config).Editable) throw new InvalidOperationException("The saved routing format cannot be overwritten");
        var raw = JsonSerializer.SerializeToElement(settings);
        if (!Read(new AppConfig { MixerRouting = raw }).Editable) throw new ArgumentException("Invalid mixer routing", nameof(settings));
        config.MixerRouting = raw;
        // Only representable legacy rules are mirrored. Named routes cannot be expressed by an old binary.
        if (Legacy(settings.Default) is { } def) config.RouteDefault = def;
        config.AppRoutes = settings.Apps.Where(p => Legacy(p.Value) != null)
            .ToDictionary(p => p.Key, p => Legacy(p.Value)!.Value);
    }

    private static AudioRoute? Legacy(MixerRoute r) => r.Output.Kind switch
    {
        MixerOutputKind.Local => AudioRoute.Local,
        MixerOutputKind.Current => r.AlsoLocal ? AudioRoute.Both : AudioRoute.HomePod,
        _ => null,
    };

    private static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 2048 && !key.Any(char.IsControl);
    private static bool Valid(MixerRoute? route) => route?.Output is { } output && Enum.IsDefined(output.Kind) &&
        (output.Kind is MixerOutputKind.Current or MixerOutputKind.Local || ValidKey(output.Id)) &&
        (output.Kind != MixerOutputKind.Speaker || QualifiedSpeakerId(output.Id!)) &&
        (output.Kind != MixerOutputKind.Local || !route.AlsoLocal);

    private static bool QualifiedSpeakerId(string id) => new[] { "airplay:", "dlna:", "local:" }
        .Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && id.Length > prefix.Length);

    public static string SpeakerKey(string qualifiedId)
    {
        int colon = qualifiedId.IndexOf(':');
        string kind = qualifiedId[..colon].ToLowerInvariant(), id = qualifiedId[(colon + 1)..];
        return "speaker:" + kind + ":" + (kind == "airplay" ? GroupPlan.Key(id) :
            kind == "dlna" ? DlnaGroupSettings.Key(id) : LocalGroupSettings.Key(id));
    }
}

/// <summary>A detached output/latency projection. Only its owning controller may mutate it.</summary>
public sealed record ResolvedMixerDestination(string Key, string Name, AppConfig? Config, MixerRoutingError Error)
{
    public bool IsLocal => Key == "local";
    public bool Available => Error == MixerRoutingError.None;
}

public static class MixerDestinationResolver
{
    public static ResolvedMixerDestination Resolve(AppConfig source, MixerRoute route, IReadOnlyList<AirPlayDevice>? scan = null)
    {
        scan ??= [];
        if (route.Output.Kind == MixerOutputKind.Local) return new("local", "", null, MixerRoutingError.None);
        var c = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(source))!;
        c.AutoConnect = false;
        string key, name;
        OutputGroupSettings selection;
        if (route.Output.Kind == MixerOutputKind.Group)
        {
            var preset = source.SavedOutputGroups.FirstOrDefault(p => string.Equals(p.Id, route.Output.Id, StringComparison.OrdinalIgnoreCase));
            if (preset == null) return Missing("group:" + route.Output.Id);
            key = "group:" + preset.Id.ToLowerInvariant(); name = preset.Name; selection = OutputGroups.Clone(preset.Settings);
        }
        else if (route.Output.Kind == MixerOutputKind.Speaker)
        {
            string id = route.Output.Id!;
            key = MixerRoutes.SpeakerKey(id);
            if (id.StartsWith("airplay:", StringComparison.OrdinalIgnoreCase))
            {
                id = id[8..];
                var row = source.GroupSpeakers?.FirstOrDefault(p => GroupPlan.Key(p.DeviceId) == GroupPlan.Key(id));
                var manual = source.ManualDevices.FirstOrDefault(p => GroupPlan.Key(p.DeviceId) == GroupPlan.Key(id));
                var found = scan.FirstOrDefault(p => GroupPlan.Key(p.DeviceId) == GroupPlan.Key(id));
                bool current = source.DeviceId != null && GroupPlan.Key(source.DeviceId) == GroupPlan.Key(id);
                if (row == null && manual == null && found == null && !current) return Missing(key);
                name = row?.Name ?? manual?.Name ?? found?.Name ?? source.DeviceName ?? id;
                selection = new() { DeviceId = id, DeviceName = name };
            }
            else if (id.StartsWith("dlna:", StringComparison.OrdinalIgnoreCase))
            {
                var output = source.GroupDlnaOutputs.FirstOrDefault(p => DlnaGroupSettings.Key(p.Udn) == DlnaGroupSettings.Key(id[5..]));
                if (output == null) return Missing(key);
                name = output.Name;
                selection = new() { GroupCaptureDeviceIds = [], GroupDlnaOutputs = [output], GroupCaptureDlnaUdns = [output.Udn] };
            }
            else
            {
                var output = source.GroupLocalOutputs.FirstOrDefault(p => LocalGroupSettings.Key(p.DeviceId) == LocalGroupSettings.Key(id[6..]));
                if (output == null) return Missing(key);
                name = output.Name;
                selection = new() { GroupCaptureDeviceIds = [], GroupLocalOutputs = [output], GroupCaptureLocalDeviceIds = [output.DeviceId] };
            }
        }
        else
        {
            selection = OutputGroups.Capture(source);
            var selected = source.SavedOutputGroups.FirstOrDefault(p => string.Equals(p.Id, source.SelectedOutputGroupId,
                StringComparison.OrdinalIgnoreCase) && Fingerprint(p.Settings) == Fingerprint(selection));
            var group = GroupPlan.FromConfig(source);
            if (selected != null) { key = "group:" + selected.Id.ToLowerInvariant(); name = selected.Name; }
            else if (group == null && !string.IsNullOrWhiteSpace(source.DeviceId))
            { key = MixerRoutes.SpeakerKey("airplay:" + source.DeviceId); name = source.DeviceName ?? source.DeviceId; }
            else if (group != null && (group.Members.Count > 0 || group.PairTsid != null || group.LocalOutputs.Count > 0 || group.DlnaOutputs.Count > 0))
            { key = "current:" + Fingerprint(selection); name = group.Name; }
            else return new("current:none", "", null, MixerRoutingError.NoSelection);
        }
        OutputGroups.Apply(c, selection);
        c.Host = UsageProfiles.FreshHost(c.DeviceId, source, scan);
        c.MultiRoomHost = UsageProfiles.FreshHost(c.MultiRoomDeviceId, source, scan);
        if (c.GroupSpeakers != null) foreach (var member in c.GroupSpeakers)
            member.Host = UsageProfiles.FreshHost(member.DeviceId, source, scan);
        if (route.AlsoLocal && LocalGroupSettings.Selected(c).Count > 0)
            return new(key, name, null, MixerRoutingError.DuplicateLocal);
        var settings = MixerRoutes.Read(source).Settings;
        if (route.Output.Kind != MixerOutputKind.Current && settings?.DestinationSettings.GetValueOrDefault(key) is { } delay)
        { c.Scene = delay.Scene; c.LatencyMs = delay.LatencyMs; c.FifoTargetMs = delay.FifoTargetMs; }
        c.MixerRouting = null;
        return new(key, name, c, MixerRoutingError.None);
    }

    private static ResolvedMixerDestination Missing(string key) => new(key, key, null, MixerRoutingError.MissingDestination);

    private static string Fingerprint(OutputGroupSettings settings)
    {
        var copy = OutputGroups.Clone(settings);
        OutputGroups.Normalize(copy);
        copy.Host = copy.MultiRoomHost = null;
        if (copy.GroupSpeakers != null) foreach (var row in copy.GroupSpeakers) row.Host = null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy))));
    }
}
