using System.Diagnostics;

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
        var exe = Environment.ProcessPath!.Replace("'", "''");
        var command = "New-NetFirewallRule -DisplayName HomePodCast -Direction Inbound " +
                      $"-Program '{exe}' -Action Allow -Profile Private -RemoteAddress LocalSubnet";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{command}\"")
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
}
