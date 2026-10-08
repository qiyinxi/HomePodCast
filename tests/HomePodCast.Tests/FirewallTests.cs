using System.Diagnostics;
using System.Text;

namespace HomePodCast.Tests;

public class FirewallTests
{
    private static readonly string[] Paths =
    [
        @"C:\Program Files\HomePodCast\HomePodCast.exe",
        @"C:\Users\O'Brien\Apps\HomePodCast.exe",
        @"D:\it’s ‘mine’ ‚x‛\HomePodCast.exe",                    // PowerShell reads these as single quotes too
        @"D:\$env:TEMP\`$(Remove-Item x)`n\Home Pod Cast.exe",   // nothing may expand
        @"D:\a ""quoted"" “dir” — 音箱\HomePodCast.exe",
        @"D:\end with quote'\'';calc;'\HomePodCast.exe",
    ];

    [Fact]
    public void Rule_command_carries_the_path_as_one_single_quoted_literal()
    {
        Assert.Equal("'C:\\Program Files\\x.exe'", Firewall.PowerShellLiteral(@"C:\Program Files\x.exe"));
        Assert.Equal("'O''Brien'", Firewall.PowerShellLiteral("O'Brien"));
        Assert.Equal("'it’’s ‘‘a’’ ‚‚b‛‛'", Firewall.PowerShellLiteral("it’s ‘a’ ‚b‛"));
        Assert.Equal("'$x `n \"y\"'", Firewall.PowerShellLiteral("$x `n \"y\""));

        foreach (var exe in Paths)
        {
            var args = Firewall.RuleArguments(exe);
            Assert.StartsWith("-NoProfile -NonInteractive -EncodedCommand ", args);
            var encoded = args["-NoProfile -NonInteractive -EncodedCommand ".Length..];
            Assert.Matches("^[A-Za-z0-9+/=]+$", encoded); // nothing for a command line to interpret
            var script = Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
            Assert.Equal(Firewall.RuleScript(exe), script);
            Assert.Contains($"-Program {Firewall.PowerShellLiteral(exe)} -Action Allow", script);
        }
    }

    [Fact]
    public async Task PowerShell_reads_every_literal_back_as_the_exact_path()
    {
        // Let Windows PowerShell itself parse the literals (the same -EncodedCommand route the rule takes) and hand
        // back what it got, as base64 of UTF-8 so the console code page cannot change anything.
        var script = string.Join("; ", Paths.Select(p =>
            $"[Console]::Out.WriteLine([Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes({Firewall.PowerShellLiteral(p)})))"));
        var psi = new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(p.ExitCode == 0, await errors);
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(Paths, lines.Select(l => Encoding.UTF8.GetString(Convert.FromBase64String(l))));
    }
}
