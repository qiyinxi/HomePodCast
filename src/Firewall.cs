using System.Diagnostics;
using System.Text;

namespace HomePodCast;

/// <summary>
/// The speaker opens connections back to us (NTP timing, retransmit requests), so the exe needs an
/// inbound allow rule. We only check for it here; adding it is done by an elevated PowerShell the user
/// explicitly approves via UAC. The MSI adds the same rule as "HomePodCast (installer)" (installer\HomePodCast.wxs);
/// rules are matched by program path, not by name, so either one counts. Both rules are for Private networks only:
/// on a Public network they don't apply (see <see cref="NetworkProfile"/>).
/// The Microsoft Store version never adds a rule: its package brings two (packaging/msix/AppxManifest.xml, TCP and UDP,
/// Private networks), which Windows creates at install for the exe's path in the package's folder under WindowsApps
/// (a new folder for every version) and removes at uninstall. A rule may also name the package by its SID
/// (INetFwRule3.LocalAppPackageId); both count.
/// </summary>
public static class Firewall
{
    // NET_FW_PROFILE2
    private const int DomainProfile = 1, PrivateProfile = 2, PublicProfile = 4;

    public static bool HasInboundAllowRule() => Inspect().PrivateRule;

    /// <summary>
    /// A rule with this program (<paramref name="ruleApplication"/>) and package (<paramref name="rulePackageSid"/>) is
    /// for this exe: the same path (case-insensitive), or, for the Store version (<paramref name="packageSid"/> set),
    /// the path with its environment variables expanded or the package's SID.
    /// </summary>
    internal static bool IsForThisApp(string? ruleApplication, string? rulePackageSid, string exe, string? packageSid)
    {
        if (ruleApplication != null && string.Equals(ruleApplication, exe, StringComparison.OrdinalIgnoreCase)) return true;
        if (packageSid == null) return false;
        if (ruleApplication != null && ruleApplication.Contains('%') &&
            string.Equals(Environment.ExpandEnvironmentVariables(ruleApplication), exe, StringComparison.OrdinalIgnoreCase))
            return true;
        return !string.IsNullOrEmpty(rulePackageSid) && string.Equals(rulePackageSid, packageSid, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A rule's LocalAppPackageId (INetFwRule3), or null if it has none or can't say.</summary>
    private static string? PackageSidOf(object rule)
    {
        try { return (string?)((dynamic)rule).LocalAppPackageId; }
        catch (Exception) { return null; }
    }

    /// <summary>What Windows Firewall lets in for this exe. Enumerates every rule (COM): call it off the UI thread if you can.</summary>
    public static FirewallRules Inspect()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var packageSid = AppEnvironment.IsPackaged ? AppEnvironment.PackageSid ?? "" : null;
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return FirewallRules.Unknown;
            dynamic policy = Activator.CreateInstance(policyType)!;
            bool privateRule = false, publicRule = false, blockRule = false;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                // NET_FW_RULE_DIR_IN = 1; NET_FW_ACTION_BLOCK = 0, NET_FW_ACTION_ALLOW = 1
                if (packageSid == null)
                {
                    if (app == null || !string.Equals(app, exe, StringComparison.OrdinalIgnoreCase) || !rule.Enabled || rule.Direction != 1)
                        continue;
                }
                else if (!rule.Enabled || rule.Direction != 1 ||
                         !(IsForThisApp(app, null, exe, packageSid) || IsForThisApp(null, PackageSidOf((object)rule), exe, packageSid)))
                {
                    continue; // the package's SID is only read for rules whose path isn't ours
                }
                int profiles = rule.Profiles, action = rule.Action;
                if (action == 1)
                {
                    privateRule |= (profiles & PrivateProfile) != 0;
                    publicRule |= (profiles & PublicProfile) != 0;
                }
                else if (action == 0 && (profiles & (PrivateProfile | PublicProfile)) != 0)
                {
                    blockRule = true;
                }
            }
            bool publicOff;
            try { publicOff = !(bool)policy.FirewallEnabled[PublicProfile]; }
            catch (Exception) { publicOff = false; }
            return new FirewallRules(true, privateRule, publicRule || publicOff, blockRule);
        }
        catch (Exception ex)
        {
            Log.Warn($"firewall check failed: {ex.Message}");
            return FirewallRules.Unknown;
        }
    }

    /// <summary>The firewall profiles active now (NET_FW_PROFILE2 bits: 1 domain, 2 private, 4 public); 0 if unknown.</summary>
    public static int CurrentProfileTypes()
    {
        try
        {
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return 0;
            dynamic policy = Activator.CreateInstance(policyType)!;
            return (int)policy.CurrentProfileTypes & (DomainProfile | PrivateProfile | PublicProfile);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Ask (via UAC) to add a Private-profile, local-subnet-only inbound rule for this exe. Never for the Store version.</summary>
    public static bool RequestRule()
    {
        if (AppEnvironment.IsPackaged)
        {
            Log.Warn("firewall: the Microsoft Store version never adds a rule (its package brings them)");
            return false;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", RuleArguments(Environment.ProcessPath!))
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            // Still running after 20 s (slow PowerShell start): ExitCode would throw, from the TrayApp constructor.
            if (p == null) return false;
            if (!p.WaitForExit(20000)) return HasInboundAllowRule();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // user declined UAC
        }
    }

    /// <summary>
    /// powershell.exe arguments that add the rule for <paramref name="exe"/>. The script goes in as -EncodedCommand
    /// (base64 of UTF-16LE), so no command line ever parses the path; inside the script it is a single-quoted literal.
    /// </summary>
    internal static string RuleArguments(string exe) =>
        "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(RuleScript(exe)));

    internal static string RuleScript(string exe) =>
        "$ErrorActionPreference = 'Stop'; New-NetFirewallRule -DisplayName HomePodCast -Direction Inbound " +
        $"-Program {PowerShellLiteral(exe)} -Action Allow -Profile Private -RemoteAddress LocalSubnet";

    /// <summary>
    /// <paramref name="s"/> as a PowerShell single-quoted string: nothing inside is expanded ($, backticks, double
    /// quotes), and every character PowerShell reads as a single quote (' and the curly ‘ ’ ‚ ‛) is doubled.
    /// </summary>
    internal static string PowerShellLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 8).Append('\'');
        foreach (char c in s)
        {
            if (c is '\'' or '\u2018' or '\u2019' or '\u201A' or '\u201B') sb.Append(c);
            sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }
}

/// <summary>Windows Firewall's inbound rules for this exe, as far as the speaker's connections back to us go.</summary>
/// <param name="Known">The rules could be read.</param>
/// <param name="PrivateRule">An enabled inbound allow rule covers Private networks (ours or the installer's).</param>
/// <param name="PublicAllowed">Inbound traffic gets through on a Public network: a rule covers Public, or the firewall is off there.</param>
/// <param name="BlockRule">An enabled inbound block rule for this exe covers Private or Public networks (block beats allow).</param>
public readonly record struct FirewallRules(bool Known, bool PrivateRule, bool PublicAllowed, bool BlockRule = false)
{
    /// <summary>Couldn't read the rules: as if ours were there (don't nag), and Public networks blocked as by default.</summary>
    public static FirewallRules Unknown => new(false, true, false);
}
