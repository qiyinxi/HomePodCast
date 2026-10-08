using System.Diagnostics;
using System.Text;

namespace HomePodCast;

/// <summary>
/// The speaker opens connections back to us (NTP timing, retransmit requests), so the exe needs an
/// inbound allow rule. We only check for it here; adding it is done by an elevated PowerShell the user
/// explicitly approves via UAC. The MSI adds the same rule as "HomePodCast (installer)" (installer\HomePodCast.wxs);
/// rules are matched by program path, not by name, so either one counts.
/// </summary>
public static class Firewall
{
    public static bool HasInboundAllowRule()
    {
        try
        {
            var exe = Environment.ProcessPath ?? "";
            var policyType = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (policyType == null) return true;
            dynamic policy = Activator.CreateInstance(policyType)!;
            foreach (dynamic rule in policy.Rules)
            {
                string? app = rule.ApplicationName;
                // NET_FW_RULE_DIR_IN = 1, NET_FW_ACTION_ALLOW = 1, NET_FW_PROFILE2_PRIVATE = 2
                if (app != null && string.Equals(app, exe, StringComparison.OrdinalIgnoreCase) &&
                    rule.Enabled && rule.Direction == 1 && rule.Action == 1 && ((int)rule.Profiles & 2) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"firewall check failed: {ex.Message}");
            return true; // don't nag if we can't tell
        }
    }

    /// <summary>Ask (via UAC) to add a Private-profile, local-subnet-only inbound rule for this exe.</summary>
    public static bool RequestRule()
    {
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
