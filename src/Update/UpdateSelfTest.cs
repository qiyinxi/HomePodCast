namespace HomePodCast.Update;

/// <summary>
/// <c>HomePodCast.exe update-selftest --feed http://127.0.0.1:PORT/... [--channel beta|stable] [--no-install]</c>:
/// what 设置 → 软件更新 「检查更新」 and then 「更新」 do (<see cref="UpdateFlow"/>: check, pick the newest release on the
/// channel, download, SHA-256 check, installer helper, exit), without a window or a question, against a fake release
/// list on this PC. CI (tools/ci/update-e2e.ps1) installs an older MSI, serves this build's MSI on 127.0.0.1 and runs
/// this from the installed exe; the helper then installs the MSI and starts the new version with --updated-from=.
///
/// The feed is only http://127.0.0.1 or http://localhost (<see cref="ReleaseFeed.Loopback"/>) and only on this command
/// line, so it cannot redirect anyone's updates; it never contacts GitHub. It reads and writes no settings: no skipped
/// version, the channel from --channel (else the default for this build). --no-install stops after the SHA-256 check
/// and removes the download, also in a portable copy (the button would open the release page there).
/// </summary>
internal static class UpdateSelfTest
{
    public const string Command = "update-selftest";

    /// <summary>Exit codes. Bad arguments and errors (HTTP, …) are 1.</summary>
    public const int HelperStarted = 0, NoUpdate = 2, VerificationFailed = 3, NotInstalled = 4;

    public const string Usage =
        "  update-selftest --feed http://127.0.0.1:PORT/PATH [--channel beta|stable] [--no-install]\n" +
        "                                         the update button against a fake release list on this PC (CI only)";

    /// <param name="Channel">null: the default for this build (测试版 for a prerelease).</param>
    internal sealed record Options(ReleaseFeed Feed, UpdateChannel? Channel, bool NoInstall);

    /// <summary>The command line (with or without the command word). Throws <see cref="ArgumentException"/> with what is wrong.</summary>
    internal static Options ParseArguments(IReadOnlyList<string> args)
    {
        string? feed = null, channel = null;
        bool noInstall = false, command = false;
        for (int i = 0; i < args.Count; i++)
        {
            string Value(ref string? field)
            {
                if (field != null) throw new ArgumentException($"{args[i]} is given twice");
                if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"{args[i]} needs a value");
                return field = args[++i];
            }

            switch (args[i])
            {
                case Command when !command: command = true; break;
                case "--feed": Value(ref feed); break;
                case "--channel": Value(ref channel); break;
                case "--no-install": noInstall = true; break;
                case "--verbose": break; // Program.Main
                default: throw new ArgumentException($"unknown argument \"{args[i]}\"");
            }
        }
        if (feed == null) throw new ArgumentException("--feed is required (an http://127.0.0.1 or http://localhost URL)");
        UpdateChannel? ch = channel?.ToLowerInvariant() switch
        {
            null => null,
            "beta" => UpdateChannel.Beta,
            "stable" => UpdateChannel.Stable,
            _ => throw new ArgumentException($"--channel is beta or stable, not \"{channel}\""),
        };
        return new Options(ReleaseFeed.Loopback(feed), ch, noInstall);
    }

    public static int Run(string[] args)
    {
        Options options;
        try
        {
            options = ParseArguments(args);
        }
        catch (ArgumentException ex)
        {
            Log.Error($"{Command}: {ex.Message}");
            Console.WriteLine(Usage);
            return 1;
        }
        return RunAsync(options, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static async Task<int> RunAsync(Options o, CancellationToken ct)
    {
        var current = ReleaseVersion.Parse(ReleaseVersion.CurrentText) ?? ReleaseVersion.Parse("0.0.0")!;
        var channel = GitHubReleases.EffectiveChannel(o.Channel, current);
        bool msi = UpdateFlow.IsMsiInstall();
        Log.Info($"{Command}: running {ReleaseVersion.CurrentText} ({(msi ? "MSI install" : "portable")}, {Environment.ProcessPath}), " +
                 $"channel {channel}{(o.NoInstall ? ", --no-install" : "")}: GET {o.Feed.ApiUrl}");

        // 「检查更新」
        var releases = await UpdateFlow.FetchReleasesAsync(o.Feed, ct);
        var result = GitHubReleases.Pick(releases, channel, current, skipped: null, needMsi: msi || o.NoInstall);
        Log.Info($"{Command}: {releases.Count} releases; newest on the {channel} channel: {result.Release?.Version.ToString() ?? "none"} -> {result.Outcome}");
        if (result is not { Outcome: CheckOutcome.UpdateAvailable, Release: { } release }) return NoUpdate;

        // 「更新」
        switch (UpdateFlow.Route(release, msi || o.NoInstall))
        {
            case InstallRoute.ReleasePage:
                Log.Info($"{Command}: portable copy: the update button opens {release.PageUrl} here and installs nothing (--no-install checks the download)");
                return NotInstalled;
            case InstallRoute.NoChecksum:
                Log.Warn($"{Command}: {release.Version}: the list has no SHA-256 for {release.Msi?.Name ?? "the MSI"}; not installing");
                return NotInstalled;
        }
        // A running HomePodCast would be closed by the installer and then meet the copy the helper starts: quit it first.
        var instance = @"Local\HomePodCast.Single" + (AppConfig.Profile is { } p ? "." + p : ""); // Program.RunGui
        if (!o.NoInstall && Mutex.TryOpenExisting(instance, out var running))
        {
            running.Dispose();
            Log.Error($"{Command}: HomePodCast is running; quit it first (its update button does this itself)");
            return 1;
        }

        DownloadedUpdate update;
        try
        {
            update = await UpdateFlow.DownloadAsync(o.Feed, release, new QuarterProgress(), ct);
        }
        catch (UpdateVerificationException ex)
        {
            Log.Warn($"update: {release.Msi!.Name}: {ex.Message}; deleted, not installed");
            return VerificationFailed;
        }
        if (o.NoInstall)
        {
            UpdatePackage.TryDelete(update.Folder);
            Log.Info($"{Command}: --no-install: {release.Version} downloaded and matched its SHA-256; removed again, nothing installed");
            return HelperStarted;
        }
        UpdateFlow.StartInstaller(update, tray: true);
        Log.Info($"{Command}: exiting so that the helper can install {release.Version} (it waits for pid {Environment.ProcessId})");
        return HelperStarted;
    }

    /// <summary>One log line per quarter, as the download reports it (Progress&lt;T&gt; would post them to the thread pool, out of order).</summary>
    private sealed class QuarterProgress : IProgress<double>
    {
        private int _reported;

        public void Report(double value)
        {
            int quarter = (int)(Math.Clamp(value, 0, 1) * 4);
            if (quarter <= _reported) return;
            _reported = quarter;
            Log.Info($"{Command}: downloaded {quarter * 25}%");
        }
    }
}
