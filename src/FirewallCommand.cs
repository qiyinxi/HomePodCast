namespace HomePodCast;

/// <summary>
/// <c>HomePodCast.exe firewall</c>: the firewall check the app makes when it starts, without the GUI. Read-only: it prints
/// what Windows Firewall lets in for this exe (<see cref="Firewall.Inspect"/>) and the message the app would show at
/// startup on a Private network, the kind of network its rule is for (<see cref="FirewallAdvisor.Decide"/>).
/// CI runs it on the exe an MSI installed (tools/ci/msi-e2e.ps1): exit code 0 means the first start shows no firewall prompt.
/// </summary>
internal static class FirewallCommand
{
    /// <summary>Exit code 0: no prompt and no block rule; 2: a prompt (the rule is missing) or a block rule; 1: the rules can't be read.</summary>
    public static int Run()
    {
        var (code, line) = Report(Environment.ProcessPath ?? "?", Firewall.Inspect(), AppEnvironment.IsPackaged);
        Console.WriteLine(line);
        return code;
    }

    internal static (int ExitCode, string Line) Report(string exe, FirewallRules rules, bool packaged)
    {
        var prompt = FirewallAdvisor.Decide(FirewallTrigger.Startup, rules, NetworkCategory.Private, packaged);
        int code = !rules.Known ? 1 : prompt != FirewallPrompt.None || rules.BlockRule ? 2 : 0;
        return (code, $"firewall: {exe}: rules read {rules.Known}, private allow rule {rules.PrivateRule}, public allowed {rules.PublicAllowed}, " +
                      $"block rule {rules.BlockRule}; startup prompt on a Private network: {prompt}");
    }
}
