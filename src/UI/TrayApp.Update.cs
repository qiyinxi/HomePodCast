using System.Diagnostics;
using HomePodCast.UI.Controls;
using HomePodCast.Update;

namespace HomePodCast.UI;

/// <summary>What 关于 → 软件更新 and the 首页 banner show.</summary>
/// <param name="Available">A newer release (not skipped), until it is installed, skipped or the channel changes.</param>
/// <param name="Banner">The update was found by an automatic check: 首页 shows it (a manual check answers in 关于).</param>
/// <param name="MsiInstall">An MSI install (更新 installs), a portable copy (更新 opens the release page), or not known yet.</param>
/// <param name="Store">The Microsoft Store version: the Store updates it, the app never checks.</param>
internal sealed record UpdateView(string Status, TextRole Role, Release? Available, bool Checking, bool Downloading, bool Banner, bool? MsiInstall,
    bool Store, double Progress = 0, string? ErrorDetail = null);

// Updates: a check of the GitHub releases (the only request outside the LAN: on 「检查更新」, or automatically when
// turned on), the notice, and installing a newer MSI through the helper (Update/*). Nothing is installed without a click.
internal sealed partial class TrayApp
{
#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif

    private static readonly ReleaseVersion CurrentVersion = ReleaseVersion.Parse(ReleaseVersion.CurrentText) ?? ReleaseVersion.Parse("0.0.0")!;

    private readonly System.Windows.Forms.Timer _updateTimer = new();
    private readonly ReleaseFeed _updateFeed;
    private DateTime _updateDue;
    private string? _noAutomaticUpdates;
    private Task<bool>? _msiInstall;
    private bool _store, _checking, _updateBanner, _installing;
    private CancellationTokenSource? _download;
    private Release? _update;
    private NotesRange _updateNotes = new([], false);
    private ReleaseNotesWindow? _releaseNotes;
    private double _updateProgress;
    private string? _updateErrorDetail;
    private string _updateStatus = "";
    private TextRole _updateRole = TextRole.Secondary;
    private Action? _balloonClick;

    public UpdateView UpdateState => new(_updateStatus, _updateRole, _update, _checking, _download != null, _updateBanner && _update != null,
        _msiInstall is { IsCompletedSuccessfully: true } t ? t.Result : null, _store, _updateProgress, _updateErrorDetail);

    /// <summary>正式版 or 测试版, as chosen, else by the running version.</summary>
    public UpdateChannel UpdateChannel => GitHubReleases.EffectiveChannel(Config.UpdateChannel, CurrentVersion);

    /// <summary>Why this build never checks by itself (a development build or test profile); null for a release build.</summary>
    public string? NoAutomaticUpdates => _noAutomaticUpdates;

    /// <summary>Called once from the constructor, after the tray icon and the window exist.</summary>
    /// <param name="updatedFrom">--updated-from=: the helper started us after installing (or trying to install) an update.</param>
    private void InitUpdate(string? updatedFrom)
    {
        // The Microsoft Store version (AppEnvironment.IsPackaged): the Store updates it, and Store policy wants the app's
        // own update check off, so it never asks GitHub (its package has no internetClient capability).
        _store = AppEnvironment.IsPackaged;
        _noAutomaticUpdates = _store ? "Microsoft Store package" : ReleaseVersion.WhyNotAutomatic(ReleaseVersion.CurrentText, AppConfig.Profile, DebugBuild);
        _updateStatus = _store ? L.T("Microsoft Store 版由 Microsoft Store 负责更新。") : LastCheckText();
        _tray.BalloonTipClicked += (_, _) =>
        {
            var click = _balloonClick;
            _balloonClick = null;
            click?.Invoke();
        };
        _updateTimer.Tick += OnUpdateTimer;
        // The window was built before this (TrayApp's constructor): its 软件更新 section still shows the GitHub rows.
        // Seen 2026-10-12 in the Store package: 检查更新 and 更新渠道 stayed until something else refreshed them.
        RaiseUpdateChanged();

        // Download folders of earlier updates (and what their helper wrote). Never from a test profile: the real
        // instance may be in the middle of one.
        if (AppConfig.Profile == null)
            Task.Run(() => UpdatePackage.CleanUp(Path.GetTempPath(), AppConfig.Directory))
                .ContinueWith(t => _ui.Post(_ => ShowInstallResult(updatedFrom, t.IsCompletedSuccessfully ? t.Result : null), null));
        else if (updatedFrom != null)
            ShowInstallResult(updatedFrom, null);
        ScheduleUpdateChecks();
    }

    private void DisposeUpdate()
    {
        _updateTimer.Dispose();
        _releaseNotes?.Dispose();
        if (!_installing) _download?.Cancel();
    }

    /// <summary>"上次检查：…" while nothing was checked in this run.</summary>
    private string LastCheckText() =>
        Config.UpdateLastCheck is { } last ? L.F("上次检查：{0}", last.ToLocalTime().ToString("yyyy-MM-dd HH:mm")) : "";

    private void SetUpdateStatus(string text, TextRole role, string? errorDetail = null)
    {
        _updateStatus = text;
        _updateRole = role;
        _updateErrorDetail = errorDetail;
        RaiseUpdateChanged();
    }

    private void RaiseUpdateChanged()
    {
        if (_quitting) return;
        _form.ShowUpdate();
        if (_releaseNotes is { IsDisposed: false } notes)
        {
            if (_update != notes.Release && _download == null) notes.Close();
            else notes.ShowState(UpdateState);
        }
    }

    private void ShowBalloon(string text, ToolTipIcon icon, Action? click)
    {
        _balloonClick = click;
        _tray.ShowBalloonTip(5000, "HomePodCast", text, icon);
    }

    private Task<bool> MsiInstall() => _msiInstall ??= AppConfig.Profile != null
        ? Task.FromResult(false) // a test copy never installs over the real one
        : Task.Run(UpdateFlow.IsMsiInstall);

    // ---------------------------------------------------------------- automatic checks

    /// <summary>Arms the timer for the next automatic check, or stops it (off, or a build that never checks by itself).</summary>
    private void ScheduleUpdateChecks()
    {
        _updateTimer.Stop();
        if (NoAutomaticCheck(_store, Config.AutoUpdateCheck, _noAutomaticUpdates) is { } why)
        {
            Log.Info($"update: {why}");
            return;
        }
        _updateDue = UpdateSchedule.FirstDue(Config.UpdateLastCheck, DateTime.UtcNow);
        Log.Info($"update: automatic check due {_updateDue.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        ArmUpdateTimer();
    }

    private void ArmUpdateTimer()
    {
        _updateTimer.Interval = (int)UpdateSchedule.Wait(_updateDue, DateTime.UtcNow).TotalMilliseconds;
        _updateTimer.Start();
    }

    private void OnUpdateTimer(object? sender, EventArgs e)
    {
        _updateTimer.Stop();
        if (_quitting || !Config.AutoUpdateCheck || _noAutomaticUpdates != null) return;
        if (DateTime.UtcNow < _updateDue || _checking || _download != null)
        {
            ArmUpdateTimer();
            return;
        }
        CheckForUpdates(automatic: true);
    }

    // ---------------------------------------------------------------- checking

    /// <summary>
    /// Asks GitHub for the releases (one GET) and shows what it found. A manual check answers in 关于 (also when it
    /// fails); an automatic one stays quiet unless there is an update, which gets a balloon (once per version) and
    /// the banner on 首页.
    /// </summary>
    public async void CheckForUpdates(bool automatic = false)
    {
        if (_store || _checking || _download != null || _quitting) return;
        _checking = true;
        if (automatic) RaiseUpdateChanged();
        else SetUpdateStatus(L.T("正在检查更新…"), TextRole.Secondary);
        bool ok = false;
        try
        {
            bool msi = await MsiInstall();
            Log.Info($"update: {(automatic ? "automatic" : "manual")} check (running {ReleaseVersion.CurrentText}, " +
                     $"{(msi ? "MSI install" : "portable")}): GET {_updateFeed.ApiUrl}");
            var releases = await UpdateFlow.FetchReleasesAsync(_updateFeed, CancellationToken.None);
            var channel = UpdateChannel; // now: it may have been changed while the request was out
            var result = GitHubReleases.Pick(releases, channel, CurrentVersion, Config.UpdateSkipped, needMsi: msi);
            ok = true;
            Log.Info($"update: {releases.Count} releases; newest on the {channel} channel: {result.Release?.Version.ToString() ?? "none"} → {result.Outcome}");
            if (_quitting) return;
            Config.UpdateLastCheck = DateTime.UtcNow;
            Config.Save();
            // Keep the offered range while downloading, even if the channel changes before the window is reopened.
            _updateNotes = result is { Outcome: CheckOutcome.UpdateAvailable, Release: { } offered }
                ? ReleaseNotes.Collect(releases, CurrentVersion, offered, channel) : new([], false);
            ShowCheckResult(result, automatic);
        }
        catch (Exception ex)
        {
            // Offline, rate limited, GitHub down: one line, and only a manual check says so.
            var (note, status) = UpdateFlow.RuntimeNote(AppEnvironment.RuntimeRemoved, AppEnvironment.RuntimeFolder);
            Log.Warn($"update: check failed: {UpdateFlow.Describe(ex)}{note}");
            if (!automatic && !_quitting) SetUpdateStatus(status ?? L.T("检查失败，请稍后再试（详情见日志）"), TextRole.Critical);
        }
        finally
        {
            _checking = false;
            if (!_quitting && Config.AutoUpdateCheck && _noAutomaticUpdates == null)
            {
                _updateDue = DateTime.UtcNow + (ok ? UpdateSchedule.Interval : UpdateSchedule.RetryAfterFailure);
                ArmUpdateTimer();
            }
            RaiseUpdateChanged();
        }
    }

    private void ShowCheckResult(CheckResult result, bool automatic)
    {
        var view = CheckView(result, automatic, _update, _updateBanner, Config.UpdateNotified);
        _update = view.Update;
        _updateBanner = view.Banner;
        SetUpdateStatus(view.Status, view.Role);
        if (view.Notify && view.Update is { } release)
        {
            Config.UpdateNotified = release.Version.ToString();
            Config.Save();
            ShowBalloon(L.F("发现新版本 {0}。点这里查看。", release.Version), ToolTipIcon.Info, ShowReleaseNotes);
        }
        if (ShouldShowNotes(result.Outcome, automatic, _store)) ShowReleaseNotes();
    }

    internal static bool ShouldShowNotes(CheckOutcome outcome, bool automatic, bool store) =>
        !store && !automatic && outcome == CheckOutcome.UpdateAvailable;

    /// <summary>What a check's result does to 关于 → 软件更新 and 首页 (<see cref="ShowCheckResult"/> applies it).</summary>
    /// <param name="Update">The release 「更新」 offers (null: no 更新 button, no banner).</param>
    /// <param name="Banner">首页's banner: only for an update an automatic check found (a manual check answers in 关于).</param>
    /// <param name="Notify">The tray balloon: an automatic check's first find of this version.</param>
    internal readonly record struct UpdateCheckView(Release? Update, bool Banner, string Status, TextRole Role, bool Notify);

    /// <param name="previous">The update offered before this check (its banner stays for the same version).</param>
    /// <param name="previousBanner">Whether 首页 showed it.</param>
    /// <param name="notified">The version the balloon announced last (<see cref="AppConfig.UpdateNotified"/>).</param>
    internal static UpdateCheckView CheckView(CheckResult result, bool automatic, Release? previous, bool previousBanner, string? notified)
    {
        var release = result.Release;
        return result.Outcome switch
        {
            CheckOutcome.UpdateAvailable => new(release,
                (previous?.Version.Equals(release!.Version) == true && previousBanner) || automatic,
                L.F("发现新版本 {0}", release!.Version), TextRole.Accent,
                automatic && notified != release.Version.ToString()),
            CheckOutcome.Skipped => new(null, false, L.F("已跳过 {0}，有更新的版本时会再提示", release!.Version), TextRole.Secondary, false),
            // Never a downgrade and never a 「更新」 to an older stable release.
            CheckOutcome.PrereleaseAhead => new(null, false, L.T("当前测试版比最新正式版新，下一个正式版发布后会提示更新"), TextRole.Secondary, false),
            _ => new(null, false, L.T("已是最新版本"), TextRole.Success, false),
        };
    }

    /// <summary>Why no automatic check is scheduled (logged as "update: …"), or null when one is.</summary>
    /// <param name="notInThisBuild">Why this build never checks by itself (<see cref="NoAutomaticUpdates"/>).</param>
    internal static string? NoAutomaticCheck(bool store, bool automaticOn, string? notInThisBuild) =>
        store ? "Microsoft Store package: never contacts GitHub (the Store updates it)"
        : !automaticOn ? "automatic checks are off (only 「检查更新」 contacts GitHub)"
        : notInThisBuild != null ? $"no automatic checks in this build ({notInThisBuild})"
        : null;

    // ---------------------------------------------------------------- settings

    public void SetAutoUpdateCheck(bool on)
    {
        if (Config.AutoUpdateCheck == on) return;
        Config.AutoUpdateCheck = on;
        Config.Save();
        Log.Info($"update: automatic checks {(on ? "on" : "off")}");
        ScheduleUpdateChecks();
        RaiseUpdateChanged();
    }

    /// <summary>A new channel: what was found for the old one no longer counts. Checks again only when automatic checks are on.</summary>
    public void SetUpdateChannel(UpdateChannel channel)
    {
        if (Config.UpdateChannel == channel) return;
        Config.UpdateChannel = channel;
        Config.Save();
        Log.Info($"update: channel {channel}");
        if (_download != null) return; // the download already chosen goes on (or is cancelled by the user)
        if (_checking) return;         // the check under way picks for the new channel
        _update = null;
        _updateBanner = false;
        if (Config.AutoUpdateCheck && _noAutomaticUpdates == null) CheckForUpdates();
        else SetUpdateStatus(L.T("渠道已更改，点「检查更新」查看这个渠道的最新版本"), TextRole.Secondary);
    }

    /// <summary>「跳过此版本」: not offered again; a newer version is.</summary>
    public void SkipUpdate()
    {
        if (_update is not { } release || _download != null) return;
        Config.UpdateSkipped = release.Version.ToString();
        Config.Save();
        Log.Info($"update: {release.Version} skipped");
        _update = null;
        _updateBanner = false;
        SetUpdateStatus(L.F("已跳过 {0}，有更新的版本时会再提示", release.Version), TextRole.Secondary);
    }

    /// <summary>All entry points share one window and the already fetched release list.</summary>
    public void ShowReleaseNotes()
    {
        if (_store || _quitting || _update is not { } release) return;
        if (_releaseNotes is { IsDisposed: false } existing && existing.Release == release)
        {
            existing.Show(); existing.Activate(); return;
        }
        _releaseNotes?.Close();
        var notes = new ReleaseNotesWindow(release, _updateNotes, L.Language,
            () => InstallUpdate(_releaseNotes, confirmed: true), CancelUpdateDownload, SkipUpdate, OpenReleaseUrl);
        _releaseNotes = notes;
        notes.FormClosed += (_, _) => { if (_releaseNotes == notes) _releaseNotes = null; notes.Dispose(); };
        notes.ShowState(UpdateState);
        notes.Show(_form);
    }

    private static void OpenReleaseUrl(string url) { if (ReleaseNotes.CanOpen(url)) OpenUrl(url); }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            Log.Info($"update: opened {url}");
        }
        catch (Exception ex)
        {
            Log.Warn($"open {url}: {ex.Message}");
        }
    }

    public void CancelUpdateDownload() => _download?.Cancel();

    // ---------------------------------------------------------------- installing

    /// <summary>
    /// 「更新」 opens the notes first; its button consents to installing. A portable copy opens the release page. An MSI install downloads the MSI, checks its
    /// SHA-256 against GitHub's, starts the helper and quits the way 退出 does (streaming stopped, per-app volumes
    /// and players restored, settings saved). The helper installs through UAC and starts the app again.
    /// </summary>
    public async void InstallUpdate(IWin32Window? owner, bool confirmed = false)
    {
        if (_store || _update is not { } release || _download != null || _checking || _quitting) return;
        if (!confirmed) { ShowReleaseNotes(); return; }
        bool msi = await MsiInstall();
        if (_quitting || _update != release || _download != null) return;
        switch (UpdateFlow.Route(release, msi))
        {
            case InstallRoute.ReleasePage:
                Log.Info($"update: portable copy, so {release.Version} is not installed here: opening its release page");
                OpenUrl(release.PageUrl);
                SetUpdateStatus(L.T("便携版不会自动安装：请在打开的发布页下载新的 zip，解压后替换旧文件。"), TextRole.Secondary);
                return;
            case InstallRoute.NoChecksum:
                Log.Warn($"update: {release.Version}: GitHub lists no SHA-256 for {release.Msi?.Name ?? "the MSI"}; not installing");
                SetUpdateStatus(L.T("GitHub 没有提供这个安装包的 SHA-256，无法校验，所以不会自动安装。请在发布页手动下载。"), TextRole.Critical);
                return;
        }
        var asset = release.Msi!;

        if (_quitting || _update != release || _download != null) return;

        using var cts = _download = new CancellationTokenSource();
        _updateProgress = 0;
        string? folder = null; // the download's folder once it matched; removed again if the helper does not start
        try
        {
            SetUpdateStatus(L.F("正在下载 {0}… {1}%", release.Version, 0), TextRole.Secondary);
            var progress = new Progress<double>(f =>
            {
                if (_download == cts && !cts.IsCancellationRequested)
                {
                    _updateProgress = f;
                    SetUpdateStatus(L.F("正在下载 {0}… {1}%", release.Version, (int)(f * 100)), TextRole.Secondary);
                }
            });
            // Removes its folder itself when the download fails, does not match or is cancelled.
            var update = await UpdateFlow.DownloadAsync(_updateFeed, release, progress, cts.Token);
            folder = update.Folder;
            if (_quitting)
            {
                UpdatePackage.TryDelete(folder);
                return;
            }

            // Keep the channel the update came from (a beta user updated to a stable release still gets betas).
            Config.UpdateChannel ??= UpdateChannel;
            Config.Save();
            UpdateFlow.StartInstaller(update, tray: !_form.Visible);
            _installing = true;
            SetUpdateStatus(L.T("正在退出并安装更新…"), TextRole.Secondary);
            Log.Info($"update: quitting so that {release.Version} can be installed");
            Quit();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Log.Info("update: download cancelled");
            UpdatePackage.TryDelete(folder);
            SetUpdateStatus(L.T("已取消下载"), TextRole.Secondary);
        }
        catch (UpdateVerificationException ex)
        {
            Log.Warn($"update: {asset.Name}: {ex.Message}; deleted, not installed");
            UpdatePackage.TryDelete(folder);
            SetUpdateStatus(L.T("下载的安装包没有通过 SHA-256 校验，已删除，不会安装。请在发布页手动下载。"), TextRole.Critical,
                UpdateFlow.Describe(ex));
        }
        catch (Exception ex)
        {
            var (note, status) = UpdateFlow.RuntimeNote(AppEnvironment.RuntimeRemoved, AppEnvironment.RuntimeFolder);
            Log.Warn($"update: download or start of the installer failed: {UpdateFlow.Describe(ex)}{note}");
            UpdatePackage.TryDelete(folder);
            SetUpdateStatus(status ?? L.T("下载或启动安装失败，请稍后再试，或在发布页手动下载。"), TextRole.Critical, UpdateFlow.Describe(ex));
        }
        finally
        {
            if (_download == cts) _download = null;
            if (!_installing) RaiseUpdateChanged();
        }
    }

    /// <summary>
    /// The page with 软件更新, where the balloons about an install lead. It was in 设置 until the navigation was split into
    /// pages (2026-10-15); the balloons still opened 设置 after that, a page without anything about updates.
    /// </summary>
    internal const AppPage UpdatePage = AppPage.About;

    /// <summary>The balloon when the helper did not install: where the details are.</summary>
    internal static string NotInstalledBalloon => L.F("更新没有完成，详情见「{0}」→「{1}」。", L.T("关于"), L.T("软件更新"));

    /// <summary>Started by the helper (--updated-from=): a balloon that it worked, or why it did not.</summary>
    private void ShowInstallResult(string? updatedFrom, InstallResult? result)
    {
        if (_quitting) return;
        if (updatedFrom == null)
        {
            if (result != null) Log.Info($"update: an earlier install left the result \"{result.Text}\"");
            return;
        }
        if (ReleaseVersion.Parse(updatedFrom) is { } from && CurrentVersion > from)
        {
            Log.Info($"update: updated from {updatedFrom} to {ReleaseVersion.CurrentText} (msiexec: {result?.Text ?? "?"})");
            SetUpdateStatus(L.F("已更新到 {0}", ReleaseVersion.CurrentText), TextRole.Success);
            ShowBalloon(L.F("已更新到 {0}", ReleaseVersion.CurrentText), ToolTipIcon.Info, () => ShowMain(UpdatePage));
            return;
        }
        if (result is { WasCancelled: true })
        {
            Log.Info($"update: cancelled (UAC declined or msiexec cancelled: {result.Code}); still {ReleaseVersion.CurrentText}");
            SetUpdateStatus(L.T("更新已取消，仍在使用当前版本。"), TextRole.Secondary);
            ShowBalloon(L.T("更新已取消，仍在使用当前版本。"), ToolTipIcon.Info, () => ShowMain(UpdatePage));
            return;
        }
        var detail = result == null ? "?" : result.Code is { } code ? $"msiexec {code}" : result.Text;
        Log.Warn($"update: not installed ({detail}); still {ReleaseVersion.CurrentText}" + (result?.MsiLog is { } log ? $"; msiexec log: {log}" : ""));
        SetUpdateStatus(L.F("更新没有完成（{0}），仍在使用 {1}。可以在发布页下载安装包手动安装。", detail, ReleaseVersion.CurrentText), TextRole.Critical);
        ShowBalloon(NotInstalledBalloon, ToolTipIcon.Warning, () => ShowMain(UpdatePage));
    }
}
