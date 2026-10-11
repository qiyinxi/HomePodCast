using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HomePodCast.Audio;
using HomePodCast.Net;
using HomePodCast.UI;
using Xunit.Abstractions;

namespace HomePodCast.Tests;

/// <summary>
/// The Microsoft Store version (packaging/msix) against the unpackaged one: every decision that differs takes
/// "packaged" (or the package family name) as a parameter, so both sides run here without a package. The test host
/// itself is unpackaged.
/// </summary>
public class PackagedTests(ITestOutputHelper output)
{
    private const string FamilyName = "qiyinxi.HomePodCast_21rkx5qfj0b6t";
    private const string StoreExe = @"C:\Program Files\WindowsApps\qiyinxi.HomePodCast_1.0.0.0_x64__21rkx5qfj0b6t\HomePodCast.exe";

    private static readonly NetworkProfile Home = new("WLAN", AdapterKind.WiFi, "ASUS_D0", NetworkCategory.Private);
    private static readonly NetworkProfile Cafe = Home with { Name = "Cafe", Category = NetworkCategory.Public };

    [Fact]
    public void The_test_host_is_not_packaged()
    {
        Assert.False(AppEnvironment.IsPackaged);
        Assert.Null(AppEnvironment.PackageFamilyName);
        Assert.Null(AppEnvironment.PackageSid);
        Assert.Equal(AutostartMethod.RunKey, Autostart.Method);
        Assert.False(Autostart.StartedByStartupTask());
    }

    // ---------------------------------------------------------------- firewall

    [Theory]
    [InlineData(FirewallTrigger.Startup)]
    [InlineData(FirewallTrigger.Blocked)]
    [InlineData(FirewallTrigger.NetworkChanged)]
    public void Packaged_never_offers_the_rule(FirewallTrigger trigger)
    {
        foreach (var category in Enum.GetValues<NetworkCategory>())
        foreach (bool privateRule in new[] { true, false })
        foreach (bool publicAllowed in new[] { true, false })
        {
            var rules = new FirewallRules(true, privateRule, publicAllowed);
            Assert.NotEqual(FirewallPrompt.AddRule, FirewallAdvisor.Decide(trigger, rules, category, packaged: true));
            Assert.NotEqual(FirewallPrompt.AddRule, FirewallAdvisor.Decide(trigger, FirewallRules.Unknown, category, packaged: true));
        }
    }

    [Theory]
    // The manifest's rules count as there, matched or not: nothing to say at startup on a Private network...
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Private, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Unknown, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Domain, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Private, true, false, FirewallPrompt.None)]
    // ... the rules are for Private networks only, so a Public one still gets its warning ...
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, true, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, false, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Public, false, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Public, true, false, FirewallPrompt.PublicNetwork)]
    [InlineData(FirewallTrigger.Startup, NetworkCategory.Public, true, true, FirewallPrompt.None)]
    // ... and a block on a Private network is somebody else's: never the rule offer the unpackaged app shows there.
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Private, false, false, FirewallPrompt.OtherBlocker)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Private, true, false, FirewallPrompt.OtherBlocker)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Unknown, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.Blocked, NetworkCategory.Domain, false, false, FirewallPrompt.None)]
    [InlineData(FirewallTrigger.NetworkChanged, NetworkCategory.Private, false, false, FirewallPrompt.None)]
    public void Packaged_firewall_prompts(FirewallTrigger trigger, NetworkCategory category, bool privateRule, bool publicAllowed,
        FirewallPrompt expected) =>
        Assert.Equal(expected, FirewallAdvisor.Decide(trigger, new FirewallRules(true, privateRule, publicAllowed), category, packaged: true));

    [Fact]
    public void Unpackaged_firewall_prompts_are_unchanged()
    {
        // The default (unpackaged) still offers the rule where it did.
        var none = new FirewallRules(true, false, false);
        Assert.Equal(FirewallPrompt.AddRule, FirewallAdvisor.Decide(FirewallTrigger.Startup, none, NetworkCategory.Private));
        Assert.Equal(FirewallPrompt.AddRule, FirewallAdvisor.Decide(FirewallTrigger.Startup, none, NetworkCategory.Private, packaged: false));
        Assert.Equal(FirewallPrompt.AddRule, FirewallAdvisor.Decide(FirewallTrigger.Blocked, none, NetworkCategory.Private, packaged: false));
    }

    [Fact]
    public void Packaged_advisor_checks_without_a_matched_rule()
    {
        var noRule = new FirewallRules(true, false, false);
        var store = new FirewallAdvisor(() => noRule, _ => Home, packaged: true);
        Assert.Equal((FirewallPrompt.None, Home), store.Check(FirewallTrigger.Startup, null));
        Assert.Equal((FirewallPrompt.OtherBlocker, Home), store.Check(FirewallTrigger.Blocked, IPAddress.Parse("192.168.50.226")));
        var cafe = new FirewallAdvisor(() => noRule, _ => Cafe, packaged: true);
        Assert.Equal((FirewallPrompt.PublicNetwork, Cafe), cafe.Check(FirewallTrigger.Startup, null));

        var msi = new FirewallAdvisor(() => noRule, _ => Home);
        Assert.Equal((FirewallPrompt.AddRule, Home), msi.Check(FirewallTrigger.Startup, null));
    }

    [Fact]
    public void Firewall_rules_match_the_exe_and_for_the_package_also_its_sid()
    {
        const string sid = "S-1-15-2-1-2-3-4-5-6-7";
        // Unpackaged: the path, nothing else (as before).
        Assert.True(Firewall.IsForThisApp(@"C:\Program Files\HomePodCast\HomePodCast.exe", null, @"C:\Program Files\HomePodCast\HomePodCast.exe", null));
        Assert.True(Firewall.IsForThisApp(@"c:\program files\homepodcast\HOMEPODCAST.EXE", null, @"C:\Program Files\HomePodCast\HomePodCast.exe", null));
        Assert.False(Firewall.IsForThisApp(null, sid, @"C:\x\HomePodCast.exe", null));
        Assert.False(Firewall.IsForThisApp(@"%ProgramFiles%\HomePodCast\HomePodCast.exe", null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"HomePodCast\HomePodCast.exe"), null));
        Assert.False(Firewall.IsForThisApp(@"C:\Other\HomePodCast.exe", null, @"C:\Program Files\HomePodCast\HomePodCast.exe", null));

        // Packaged: the manifest's rules are stored with the exe's path in WindowsApps; a rule may also name the
        // package by its SID, or the path with environment variables.
        Assert.True(Firewall.IsForThisApp(StoreExe, null, StoreExe, sid));
        Assert.True(Firewall.IsForThisApp(null, sid, StoreExe, sid));
        Assert.True(Firewall.IsForThisApp(@"C:\Elsewhere\Other.exe", sid.ToLowerInvariant(), StoreExe, sid));
        Assert.True(Firewall.IsForThisApp(@"%ProgramFiles%\WindowsApps\qiyinxi.HomePodCast_1.0.0.0_x64__21rkx5qfj0b6t\HomePodCast.exe", null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"WindowsApps\qiyinxi.HomePodCast_1.0.0.0_x64__21rkx5qfj0b6t\HomePodCast.exe"), sid));
        Assert.False(Firewall.IsForThisApp(null, "S-1-15-2-9-9-9-9-9-9-9", StoreExe, sid));
        Assert.False(Firewall.IsForThisApp(null, "", StoreExe, ""));
        Assert.False(Firewall.IsForThisApp(@"C:\Program Files\WindowsApps\qiyinxi.HomePodCast_0.9.0.0_x64__21rkx5qfj0b6t\HomePodCast.exe", null, StoreExe, sid));
    }

    [Fact]
    public void Package_sid_is_derived_from_the_family_name()
    {
        var sid = AppEnvironment.PackageSidFor(FamilyName);
        output.WriteLine($"{FamilyName}: {sid}");
        Assert.NotNull(sid);
        Assert.Matches(@"^S-1-15-2(-\d+){7}$", sid);
        Assert.Equal(sid, AppEnvironment.PackageSidFor(FamilyName));
        Assert.NotEqual(sid, AppEnvironment.PackageSidFor("qiyinxi.Other_21rkx5qfj0b6t"));
    }

    [Fact]
    public void Firewall_messages_for_the_store_version_name_no_exe_path()
    {
        Assert.DoesNotContain("HomePodCast.exe", FirewallBlockedException.Describe(Home, packaged: true));
        Assert.Contains("HomePodCast.exe", FirewallBlockedException.Describe(Home, packaged: false));
        Assert.Equal(new FirewallBlockedException(Home).Message, FirewallBlockedException.Describe(Home, packaged: false));
        Assert.DoesNotContain("HomePodCast.exe", TrayApp.OtherBlockerText(packaged: true));
        Assert.Contains("Microsoft Store", TrayApp.OtherBlockerText(packaged: true));
        Assert.Contains("HomePodCast.exe", TrayApp.OtherBlockerText(packaged: false));
    }

    // ---------------------------------------------------------------- autostart

    [Fact]
    public void Autostart_uses_the_run_value_unpackaged_and_the_startup_task_packaged()
    {
        Assert.Equal(AutostartMethod.RunKey, Autostart.MethodFor(packaged: false));
        Assert.Equal(AutostartMethod.StartupTask, Autostart.MethodFor(packaged: true));

        Assert.True(Autostart.RepairsRunKey(profile: null, packaged: false));
        Assert.False(Autostart.RepairsRunKey(profile: null, packaged: true));   // its reads would see the MSI's value
        Assert.False(Autostart.RepairsRunKey(profile: "test", packaged: false));
        Assert.False(Autostart.RepairsRunKey(profile: "test", packaged: true));
    }

    [Theory]
    [InlineData(StartupTaskState.Disabled, false, true, nameof(AutostartNote.None))]
    [InlineData(StartupTaskState.Enabled, true, true, nameof(AutostartNote.None))]
    [InlineData(StartupTaskState.DisabledByUser, false, true, nameof(AutostartNote.OffInWindows))]
    [InlineData(StartupTaskState.DisabledByPolicy, false, false, nameof(AutostartNote.Policy))]
    [InlineData(StartupTaskState.EnabledByPolicy, true, false, nameof(AutostartNote.Policy))]
    public void Startup_task_state_shows_on_the_switch(StartupTaskState state, bool on, bool canChange, string note) =>
        Assert.Equal(new AutostartView(on, canChange, Enum.Parse<AutostartNote>(note)), Autostart.ViewOf(state));

    [Fact]
    public void Startup_task_api_fails_cleanly_without_a_package()
    {
        // The real WinRT calls (activation factory, vtable slots, the async operation): without package identity
        // Windows refuses, which must come back as an exception, never a crash.
        var ex = Record.Exception(() => StartupTaskApi.GetState(AppEnvironment.StartupTaskId));
        output.WriteLine($"GetState: {ex?.GetType().Name}: {ex?.Message}");
        Assert.NotNull(ex);
        Assert.False(ex is AccessViolationException);

        // The activation kind: an unpackaged process wasn't started by a startup task (false, or refused).
        bool started = false;
        var kind = Record.Exception(() => started = StartupTaskApi.LaunchedByStartupTask());
        output.WriteLine($"LaunchedByStartupTask: {started} {kind?.GetType().Name}: {kind?.Message}");
        Assert.False(started);
    }

    // ---------------------------------------------------------------- microphone

    [Theory]
    [InlineData(unchecked((int)0x80070005), true)]  // E_ACCESSDENIED: privacy settings
    [InlineData(unchecked((int)0x800704EC), true)]  // ERROR_ACCESS_DISABLED_BY_POLICY
    [InlineData(unchecked((int)0x88890004), false)] // AUDCLNT_E_DEVICE_INVALIDATED: unplugged, retry
    [InlineData(unchecked((int)0x8889000A), false)] // AUDCLNT_E_DEVICE_IN_USE: exclusive mode, retry
    [InlineData(unchecked((int)0x80070490), false)] // E_NOTFOUND
    [InlineData(unchecked((int)0x80004005), false)] // E_FAIL
    [InlineData(0, false)]
    public void Mic_access_denied_is_told_apart_from_device_trouble(int hresult, bool denied)
    {
        Assert.Equal(denied, MicCapture.IsAccessDenied(hresult));
        // What CoreAudio.Check throws for it (UnauthorizedAccessException for E_ACCESSDENIED) carries the HRESULT.
        if (hresult < 0) Assert.Equal(denied, MicCapture.IsAccessDenied(Marshal.GetExceptionForHR(hresult)!.HResult));
    }

    [Fact]
    public void Mic_denied_message_points_at_the_right_switch()
    {
        Assert.Contains("HomePodCast", TrayApp.MicDeniedText(packaged: true));
        Assert.DoesNotContain("桌面应用", TrayApp.MicDeniedText(packaged: true));
        Assert.Contains("桌面应用", TrayApp.MicDeniedText(packaged: false));
        Assert.Equal("ms-settings:privacy-microphone", MicCapture.PrivacySettingsUri);
    }

    // ---------------------------------------------------------------- restart, log folder

    [Fact]
    public void Restart_goes_through_the_package_when_packaged()
    {
        Assert.Equal((@"C:\Windows\explorer.exe", $@"shell:AppsFolder\{FamilyName}!HomePodCast"),
            AppEnvironment.RelaunchCommand(FamilyName, StoreExe, @"C:\Windows"));
        Assert.Equal((@"D:\Apps\HomePodCast\HomePodCast.exe", ""),
            AppEnvironment.RelaunchCommand(null, @"D:\Apps\HomePodCast\HomePodCast.exe", @"C:\Windows"));
        // This (unpackaged) host restarts its own exe.
        Assert.Equal(Environment.ProcessPath, AppEnvironment.Relaunch.File);
    }

    [Fact]
    public void Log_folder_for_explorer_is_the_packages_storage_when_the_log_is_there()
    {
        const string roaming = @"C:\Users\me\AppData\Roaming", local = @"C:\Users\me\AppData\Local";
        const string folder = roaming + @"\HomePodCast";
        const string redirected = local + @"\Packages\" + FamilyName + @"\LocalCache\Roaming\HomePodCast";
        var asked = new List<string>();
        Func<string, bool> exists(params string[] files) => path =>
        {
            asked.Add(path);
            return files.Contains(path, StringComparer.OrdinalIgnoreCase);
        };

        // Unpackaged: the folder itself, nothing looked up.
        Assert.Equal(folder, AppEnvironment.FolderForExplorer(folder, "homepodcast.log", null, roaming, local, exists()));
        Assert.Empty(asked);
        // Packaged, the log created by the package: in its storage.
        Assert.Equal(redirected, AppEnvironment.FolderForExplorer(folder, "homepodcast.log", FamilyName, roaming, local,
            exists(redirected + @"\homepodcast.log")));
        // Packaged, the log an MSI or portable copy had written before: changed in place, in the usual folder.
        Assert.Equal(folder, AppEnvironment.FolderForExplorer(folder, "homepodcast.log", FamilyName, roaming, local, exists()));
        // A test profile's folder is redirected the same way; a folder outside %APPDATA% never.
        Assert.Equal(local + @"\Packages\" + FamilyName + @"\LocalCache\Roaming\HomePodCast-x",
            AppEnvironment.FolderForExplorer(roaming + @"\HomePodCast-x", "homepodcast.log", FamilyName, roaming, local,
                exists(local + @"\Packages\" + FamilyName + @"\LocalCache\Roaming\HomePodCast-x\homepodcast.log")));
        Assert.Equal(@"D:\Logs", AppEnvironment.FolderForExplorer(@"D:\Logs", "homepodcast.log", FamilyName, roaming, local, _ => true));
        Assert.Equal(roaming, AppEnvironment.FolderForExplorer(roaming, "homepodcast.log", FamilyName, roaming, local, _ => true));

        Assert.Equal("/select,\"C:\\a b\\homepodcast.log\"", AppEnvironment.ExplorerArguments(@"C:\a b", "homepodcast.log"));
        Assert.Equal("\"C:\\a b\"", AppEnvironment.ExplorerArguments(@"C:\a b", null));
        // This (unpackaged) host: the config folder.
        Assert.Equal(AppConfig.Directory, AppEnvironment.LogFolder);
    }

    // ---------------------------------------------------------------- the manifest and the docs

    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap10 = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    private static readonly XNamespace Desktop2 = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/2";

    [Fact]
    public void Manifest_declares_what_the_app_expects()
    {
        var manifest = XDocument.Load(Path.Combine(Repo.Root, "packaging", "msix", "AppxManifest.xml"));
        var app = Assert.Single(manifest.Descendants(Foundation + "Application"));
        Assert.Equal(AppEnvironment.ApplicationId, (string?)app.Attribute("Id"));
        Assert.Equal("HomePodCast.exe", (string?)app.Attribute("Executable"));

        // The startup task: the TaskId the app switches, off until the user turns it on, started into the tray.
        var startup = Assert.Single(app.Descendants(Desktop + "Extension"), e => (string?)e.Attribute("Category") == "windows.startupTask");
        Assert.Equal("HomePodCast.exe", (string?)startup.Attribute("Executable"));
        Assert.Equal("--tray", (string?)startup.Attribute(Uap10 + "Parameters"));
        var task = Assert.Single(startup.Elements(Desktop + "StartupTask"));
        Assert.Equal(AppEnvironment.StartupTaskId, (string?)task.Attribute("TaskId"));
        Assert.Equal("false", (string?)task.Attribute("Enabled"));

        // The firewall rules the app counts on instead of asking: inbound, TCP and UDP, Private networks.
        var rules = manifest.Descendants(Desktop2 + "Rule").ToList();
        Assert.Equal(new[] { "TCP", "UDP" }, rules.Select(r => (string?)r.Attribute("IPProtocol") ?? "").Order().ToArray());
        Assert.All(rules, r => Assert.Equal(("in", "private"), ((string?)r.Attribute("Direction"), (string?)r.Attribute("Profile"))));
        // The microphone switch the mic-denied message points at.
        Assert.Contains(manifest.Descendants(Foundation + "DeviceCapability"), c => (string?)c.Attribute("Name") == "microphone");
    }

    /// <summary>
    /// A package family name is the identity name, "_", and 13 characters of base32 (Crockford's digits, lower case)
    /// of the first 8 bytes of SHA-256 over the publisher in UTF-16LE.
    /// </summary>
    internal static string FamilyNameOf(string name, string publisher)
    {
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong bits = 0;
        for (int i = 0; i < 8; i++) bits = bits << 8 | hash[i];
        const string digits = "0123456789abcdefghjkmnpqrstvwxyz";
        var id = new StringBuilder();
        for (int i = 0; i < 13; i++)
        {
            int shift = 64 - 5 * (i + 1); // 65 bits: the last group gets a zero bit appended
            id.Append(digits[(int)(shift >= 0 ? bits >> shift & 31 : bits << -shift & 31)]);
        }
        return $"{name}_{id}";
    }

    [Fact]
    public void Store_paths_in_the_docs_name_the_packages_family()
    {
        // Microsoft's own publisher hash, as a check of the algorithm.
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe",
            FamilyNameOf("Microsoft.WindowsCalculator", "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));

        var build = File.ReadAllText(Path.Combine(Repo.Root, "packaging", "msix", "build.ps1"));
        string Default(string parameter) => Regex.Match(build, $@"\[string\] \${parameter} = '([^']+)'").Groups[1].Value;
        var family = FamilyNameOf(Default("IdentityName"), Default("Publisher"));
        Assert.Equal(FamilyName, family);

        var path = $@"%LOCALAPPDATA%\Packages\{family}\LocalCache\Roaming\HomePodCast";
        foreach (var doc in new[] { "README.md", "README.zh-CN.md", "README.zh-TW.md", "README.ja.md", @"docs\device-test-checklist.zh-CN.md" })
            Assert.True(File.ReadAllText(Path.Combine(Repo.Root, doc)).Contains(path), $"{doc} doesn't name {path}");
    }
}
