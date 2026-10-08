using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace HomePodCast.Audio;

// Per-app routing. What `proctest` measured (Windows 11 26200, see the commit adding this file):
//   * process loopback, like endpoint loopback, is AFTER the app's session volume/mute and BEFORE the
//     endpoint volume/mute. Muting a session therefore removes it from every capture.
//   * a tiny session volume (1e-4 .. 1e-6) is applied in float and is exactly undone by gain on the
//     process-loopback copy, so "HomePod only" = session volume x 1e-5 (-100 dB, inaudible locally)
//     + an include-tree capture of that app with gain 1e5.
//   * process loopback delivers 10 ms packets continuously (also for silent, idle or exited targets) and
//     costs about 35 ms more than endpoint loopback, so routing is only used when a rule needs it.
//   * the system-sounds session has no process id: it cannot be captured on its own and stays local.

/// <summary>Where an app's sound goes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AudioRoute>))]
public enum AudioRoute
{
    /// <summary>Sent to the speaker. With per-app routing on, silenced on this PC.</summary>
    HomePod,
    /// <summary>Only on this PC, not sent.</summary>
    Local,
    /// <summary>Sent and audible on this PC.</summary>
    Both,
}

/// <summary>Immutable snapshot of the routing settings.</summary>
public sealed record RouteRules(AudioRoute Default, IReadOnlyDictionary<string, AudioRoute> Apps)
{
    public static readonly RouteRules Today = new(AudioRoute.HomePod, new Dictionary<string, AudioRoute>());

    public AudioRoute For(string key) => Apps.TryGetValue(key, out var r) ? r : Default;

    /// <summary>
    /// Whether per-app routing (process loopback, ~35 ms more latency) is needed: as soon as one app's
    /// destination differs from the default, or nothing is sent by default. Otherwise the whole endpoint
    /// is captured exactly as before.
    /// </summary>
    public bool NeedsRouting => Default == AudioRoute.Local || Apps.Values.Any(r => r != Default);

    /// <summary>Rule key for a process: lower-case executable name without ".exe".</summary>
    public static string KeyFor(string processOrExeName)
    {
        var name = processOrExeName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.ToLowerInvariant();
    }
}

/// <summary>An audio session on the default output device.</summary>
public readonly record struct SessionEntry(uint Pid, bool IsSystem);

/// <summary>A running process: parent and rule key (see <see cref="RouteRules.KeyFor"/>).</summary>
public readonly record struct ProcessEntry(uint Pid, uint ParentPid, string Key);

/// <summary>One process-loopback capture: the tree under <paramref name="RootPid"/>.</summary>
public sealed record RouteTarget(uint RootPid, string Key, bool Compensate);

/// <summary>A session to silence locally, and the capture (target root) that carries it to the speaker.</summary>
public readonly record struct Silence(uint SessionPid, uint TargetRoot);

/// <summary>What to capture, and which sessions to silence locally.</summary>
public sealed record RoutePlan(bool Endpoint, IReadOnlyList<RouteTarget> Targets, IReadOnlyList<Silence> Silenced)
{
    public static readonly RoutePlan EndpointOnly = new(true, [], []);

    public bool SameAs(RoutePlan other) =>
        Endpoint == other.Endpoint && Targets.SequenceEqual(other.Targets) && Silenced.SequenceEqual(other.Silenced);
}

public static class RoutePlanner
{
    private const int MaxDepth = 64;

    /// <summary>
    /// Turn the current sessions into capture targets. Multi-process apps (browsers) are captured once,
    /// from the topmost ancestor with the same executable. A process inside another captured app's tree
    /// is captured by that tree anyway, so it follows that app's destination.
    /// </summary>
    public static RoutePlan Plan(RouteRules rules, IEnumerable<SessionEntry> sessions, IReadOnlyDictionary<uint, ProcessEntry> processes)
    {
        if (!rules.NeedsRouting) return RoutePlan.EndpointOnly;

        // Session pid -> (root of its app, own route)
        var sessionRoots = new Dictionary<uint, uint>();
        var roots = new Dictionary<uint, (string Key, AudioRoute Route)>();
        foreach (var s in sessions)
        {
            if (s.IsSystem || s.Pid == 0 || !processes.TryGetValue(s.Pid, out var p)) continue;
            uint root = RootOf(p, processes);
            sessionRoots[s.Pid] = root;
            roots[root] = (p.Key, rules.For(p.Key));
        }

        // A root inside a captured root's tree follows the outermost captured ancestor.
        var owners = new Dictionary<uint, uint>();
        var targets = new List<RouteTarget>();
        foreach (var (root, (key, route)) in roots.OrderBy(r => r.Key))
        {
            var owner = CapturedAncestor(root, roots, processes) ?? root;
            owners[root] = owner;
            if (owner == root && route != AudioRoute.Local) targets.Add(new RouteTarget(root, key, route == AudioRoute.HomePod));
        }

        var silenced = sessionRoots
            .Select(kv => new Silence(kv.Key, owners[kv.Value]))
            .Where(s => roots[s.TargetRoot].Route == AudioRoute.HomePod)
            .OrderBy(s => s.SessionPid)
            .ToList();
        return new RoutePlan(false, targets, silenced);
    }

    private static uint RootOf(ProcessEntry p, IReadOnlyDictionary<uint, ProcessEntry> processes)
    {
        var root = p;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (root.ParentPid == 0 || root.ParentPid == root.Pid ||
                !processes.TryGetValue(root.ParentPid, out var parent) || parent.Key != p.Key) break;
            root = parent;
        }
        return root.Pid;
    }

    /// <summary>The outermost ancestor of <paramref name="pid"/> that is itself a sent app root, if any.</summary>
    private static uint? CapturedAncestor(uint pid, Dictionary<uint, (string Key, AudioRoute Route)> roots,
        IReadOnlyDictionary<uint, ProcessEntry> processes)
    {
        uint? found = null;
        uint current = pid;
        for (int depth = 0; depth < MaxDepth; depth++)
        {
            if (!processes.TryGetValue(current, out var p) || p.ParentPid == 0 || p.ParentPid == current) break;
            current = p.ParentPid;
            if (current == pid) break; // cycle (pid reuse)
            if (roots.TryGetValue(current, out var r) && r.Route != AudioRoute.Local) found = current;
        }
        return found;
    }
}

/// <summary>
/// "Silent here, full level on the HomePod": the session volume is multiplied by <see cref="Epsilon"/>
/// and the captured copy by 1/Epsilon. The scaled volume itself marks a session as attenuated (no human
/// sets 0.001 %), so a crashed run can be undone without any saved state.
/// </summary>
public static class SessionAttenuation
{
    /// <summary>-100 dB. proctest: 1e-4 .. 1e-6 all came back exact through process loopback.</summary>
    public const float Epsilon = 1e-5f;

    public const float Gain = 1f / Epsilon;

    /// <summary>Captured peaks above this cannot come from an attenuated session (a +12 dB float over at most).</summary>
    public const float UnattenuatedPeak = Epsilon * 4;

    public static bool IsAttenuated(float raw) => raw > 0 && raw <= Epsilon * 1.01f;

    /// <summary>The volume the user sees and sets (the session volume before attenuation).</summary>
    public static float ToLogical(float raw) => IsAttenuated(raw) ? Math.Min(1f, raw / Epsilon) : raw;

    public static float Attenuated(float raw) => raw <= Epsilon * 1.01f ? raw : raw * Epsilon;

    private static volatile IReadOnlySet<uint> _silenced = new HashSet<uint>();

    /// <summary>
    /// Processes whose sessions the running capture keeps silent here, so a volume set in the mixer
    /// is applied attenuated instead of briefly making the app audible.
    /// </summary>
    public static IReadOnlySet<uint> SilencedPids
    {
        get => _silenced;
        internal set => _silenced = value;
    }

    public static float Restored(float raw) => ToLogical(raw);

    /// <summary>Gain for one captured packet of a target whose sessions are silenced locally.</summary>
    public static float CompensationGain(bool compensate, bool confirmed, float rawPeak) =>
        compensate && confirmed && rawPeak <= UnattenuatedPeak ? Gain : 1f;
}

/// <summary>
/// When the captured audio of a "HomePod only" target may get the 1/Epsilon gain. Open only once its sessions
/// have been silenced for <c>confirmDelay</c> (so the capture, ~35-50 ms behind, carries attenuated audio) AND
/// while the router keeps re-reading their volumes (a check within <c>verifyWindow</c>). A volume raised
/// elsewhere (<see cref="Silenced"/> again), a default-device switch (<see cref="CloseAll"/>) or a router that
/// stops checking all close it, so audio nobody has verified as attenuated is never amplified 100 dB.
/// The router writes, the capture thread reads.
/// </summary>
internal sealed class CompensationGate(long confirmDelay, long verifyWindow)
{
    private readonly ConcurrentDictionary<uint, long> _openAt = new();
    private readonly ConcurrentDictionary<uint, long> _verifiedAt = new();

    /// <summary>The root's sessions were just silenced (or found raised and silenced again): closed for the delay.</summary>
    public void Silenced(uint root, long now) => _openAt[root] = now + confirmDelay;

    /// <summary>The root's sessions are silenced and unchanged: opens after the delay unless already pending/open.</summary>
    public void Keep(uint root, long now) => _openAt.TryAdd(root, now + confirmDelay);

    /// <summary>Every tracked session of the root was just read back attenuated.</summary>
    public void Verified(uint root, long now) => _verifiedAt[root] = now;

    public void Close(uint root)
    {
        _openAt.TryRemove(root, out _);
        _verifiedAt.TryRemove(root, out _);
    }

    public void CloseAll()
    {
        _openAt.Clear();
        _verifiedAt.Clear();
    }

    public bool Contains(uint root) => _openAt.ContainsKey(root);

    public IReadOnlyList<uint> Roots => [.. _openAt.Keys];

    public bool IsOpen(uint root, long now) =>
        _openAt.TryGetValue(root, out var at) && now >= at &&
        _verifiedAt.TryGetValue(root, out var checkedAt) && now - checkedAt <= verifyWindow;
}
