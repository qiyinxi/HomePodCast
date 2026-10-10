using System.Net;

namespace HomePodCast;

/// <summary>Why the firewall and the network are looked at.</summary>
public enum FirewallTrigger
{
    /// <summary>The app started.</summary>
    Startup,

    /// <summary>A connection failed: the speaker's timing requests never reached us (FirewallBlockedException).</summary>
    Blocked,

    /// <summary>An address changed and the network has had time to settle, while a connection is wanted.</summary>
    NetworkChanged,
}

/// <summary>What to tell the user.</summary>
public enum FirewallPrompt
{
    None,

    /// <summary>The speaker's network is Public, where our rule (Private only) doesn't apply: change it in Settings.</summary>
    PublicNetwork,

    /// <summary>There is no rule for this exe yet: offer to add one (UAC).</summary>
    AddRule,

    /// <summary>Blocked although the firewall lets us in: another firewall, a security program or a block rule.</summary>
    OtherBlocker,
}

/// <summary>
/// Decides which firewall or network message, if any, to show, and keeps them from nagging: each one at most once per
/// run (the Public network one once per network), never two at a time. The firewall and the network are read through
/// the two functions given (Firewall.Inspect, NetworkProfile.ForSpeaker; tests pass their own).
/// </summary>
internal sealed class FirewallAdvisor(Func<FirewallRules> rules, Func<IPAddress?, NetworkProfile> network)
{
    private readonly HashSet<string> _shown = new(StringComparer.Ordinal);
    private string? _lastLog;

    /// <summary>
    /// What to show for <paramref name="category"/>, the category of the speaker's network.
    /// Startup: as before, offer the rule if it's missing. NetworkChanged: only the Public network warning.
    /// Blocked: the cause, if it can be told; never a second copy of a rule that is already there.
    /// </summary>
    internal static FirewallPrompt Decide(FirewallTrigger trigger, FirewallRules rules, NetworkCategory category)
    {
        // Our rule only applies on Private networks: on a Public one that is the thing to fix, whatever else is true.
        if (category == NetworkCategory.Public && !rules.PublicAllowed) return FirewallPrompt.PublicNetwork;
        if (trigger == FirewallTrigger.NetworkChanged || !rules.Known) return FirewallPrompt.None;
        if (trigger == FirewallTrigger.Startup) return rules.PrivateRule ? FirewallPrompt.None : FirewallPrompt.AddRule;
        return category switch
        {
            NetworkCategory.Domain => FirewallPrompt.None, // only the network's administrator can let us in there (logged)
            _ when !rules.PrivateRule && category != NetworkCategory.Public => FirewallPrompt.AddRule,
            NetworkCategory.Private or NetworkCategory.Public => FirewallPrompt.OtherBlocker,
            _ => FirewallPrompt.None, // rule in place, network unknown: nothing more to tell
        };
    }

    /// <summary>
    /// Reads the firewall and the network that reaches <paramref name="speaker"/> (null: the default route's) and
    /// decides what to show. Slow (COM): call it off the UI thread. Never throws.
    /// </summary>
    public (FirewallPrompt Prompt, NetworkProfile Network) Check(FirewallTrigger trigger, IPAddress? speaker)
    {
        NetworkProfile net;
        FirewallRules fw;
        try { net = network(speaker); }
        catch (Exception ex)
        {
            Log.Warn($"network: profile unknown ({ex.Message})");
            net = NetworkProfile.Unknown;
        }
        try { fw = rules(); }
        catch (Exception ex)
        {
            Log.Warn($"firewall check failed: {ex.Message}");
            fw = FirewallRules.Unknown;
        }
        var prompt = Decide(trigger, fw, net.Category);
        LogChecked(speaker, net, fw, prompt);
        return (prompt, net);
    }

    /// <summary>One line per finding, repeated only when something changed (Blocked comes on every retry).</summary>
    private void LogChecked(IPAddress? speaker, NetworkProfile net, FirewallRules fw, FirewallPrompt prompt)
    {
        string line = $"network: {(speaker == null ? "default route" : "speaker reached")} via {net}";
        bool warn = true;
        if (prompt == FirewallPrompt.PublicNetwork)
            line += "; HomePodCast's firewall rule only applies on Private networks, so the speaker can't connect back";
        else if (net.Category == NetworkCategory.Domain)
            line += "; HomePodCast's firewall rule only applies on Private networks: on a domain network, only its administrator can allow HomePodCast.exe";
        else if (prompt == FirewallPrompt.OtherBlocker)
            line += fw.BlockRule
                ? "; blocked although a firewall rule allows HomePodCast.exe: an inbound block rule for it exists too"
                : "; blocked although a firewall rule allows HomePodCast.exe: another firewall or security program may be blocking it";
        else
            warn = false;
        if (Interlocked.Exchange(ref _lastLog, line) == line) return;
        if (warn) Log.Warn(line);
        else Log.Info(line);
    }

    /// <summary>A prompt is showing (UI thread).</summary>
    public bool IsOpen { get; private set; }

    /// <summary>
    /// UI thread, before showing <paramref name="prompt"/>: false if it was shown before (for a Public network: for
    /// this network) or another one is open. True marks it shown and open until <see cref="Close"/>.
    /// </summary>
    public bool TryOpen(FirewallPrompt prompt, NetworkProfile network)
    {
        if (prompt == FirewallPrompt.None || IsOpen || !_shown.Add(Key(prompt, network))) return false;
        IsOpen = true;
        return true;
    }

    public void Close() => IsOpen = false;

    internal static string Key(FirewallPrompt prompt, NetworkProfile network) =>
        prompt == FirewallPrompt.PublicNetwork ? $"{prompt}:{network.DisplayName}" : prompt.ToString();
}
