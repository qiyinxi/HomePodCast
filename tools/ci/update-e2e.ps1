<#
.SYNOPSIS
  End-to-end test of the MSI install's in-app update (设置 → 软件更新 「更新」), for CI (build.yml job update-e2e).

.DESCRIPTION
  What a person did by hand for each release, on a throwaway CI runner:
    1. builds an "old" MSI from this same source with a version below the build's (default 0.0.1-e2e.1; MSI 0.0.1.1),
       so the build's own MSI (-NewMsi, the one the build job uploaded) is the update. The old MSI never leaves the
       runner: no artifact, no release;
    2. serves a fake GitHub release list (the shape GitHubReleases.Parse reads: tag_name, prerelease, draft, assets
       with name, state, size, digest "sha256:…" and browser_download_url) and the new MSI on 127.0.0.1
       (tools/ci/fake-release-feed.ps1). Nothing goes to GitHub;
    3. for the default folder (C:\Program Files\HomePodCast) and a custom INSTALLFOLDER with spaces:
       - installs the old MSI silently (msiexec /i /qn);
       - [default folder only] serves a corrupted MSI: `HomePodCast.exe update-selftest` must refuse it (SHA-256
         mismatch, exit 3), leave no update folder, start no helper, and the old version stays installed;
       - serves the real MSI and runs the installed old exe's `update-selftest --feed http://127.0.0.1:<port>/releases`:
         the 更新 button's code (UpdateFlow: check, pick, download, SHA-256, helper, exit) without UI;
       - waits for the helper: msiexec through Verb runas, then the app started again with --updated-from=<old>;
       - checks: exactly one HomePodCast product (Windows Installer and Add/Remove Programs) and it is the new
         version, in the same folder; the relaunched process is the new exe there with --updated-from=<old> --tray;
         the app logged "update: updated from <old> to <new> (msiexec: 0)" and removed its %TEMP%\HomePodCast-update-*
         folder; Windows Installer logged the install with status 0 (event 1033); then stops the app and uninstalls.

  What only a person sees: the UAC dialog itself. GitHub-hosted runners run the job as an administrator with UAC not
  prompting, so the helper's Verb runas elevates without a dialog here; on a real PC Windows asks (and a declined
  prompt is exit code 1223, which the app reports as cancelled). Also SmartScreen on a downloaded MSI, and the
  portable zip opening the release page (docs/device-test-checklist.zh-CN.md, 「软件更新」).

  It installs and uninstalls HomePodCast, so it refuses to run outside GitHub Actions (-AllowOutsideCi for a
  throwaway VM) and whenever HomePodCast is already installed. -NoInstall runs only the parts that install nothing:
  the fake feed and `update-selftest --no-install` of -Exe (check, download, SHA-256 good and corrupted).

.EXAMPLE
  ./tools/ci/update-e2e.ps1 -NewMsi packages/HomePodCast-1.0.0-pr.5.100-x64.msi -NewVersion 1.0.0-pr.5.100

.EXAMPLE
  # Safe anywhere: nothing is installed.
  ./tools/ci/update-e2e.ps1 -NoInstall -Exe app/HomePodCast.exe -NewMsi publish/HomePodCast-1.0.0-local.1-x64.msi -NewVersion 9.9.9-local.1
#>
param(
    # The build's MSI: the update offered to the old install.
    [Parameter(Mandatory)] [string] $NewMsi,
    # The build's version (build.yml "Version" step), e.g. 1.0.0-pr.5.100, 1.0.0-beta.6, 1.0.0.
    [Parameter(Mandatory)] [string] $NewVersion,
    # The old install's version; below every real one. Built here from this source, never published.
    [string] $OldVersion = '0.0.1-e2e.1',
    [string] $WorkDir = (Join-Path ($env:RUNNER_TEMP ?? [System.IO.Path]::GetTempPath()) 'update-e2e'),
    # The second scenario's install folder (no trailing backslash).
    [string] $CustomFolder = 'C:\HomePodCast e2e\Custom Folder',
    [switch] $NoInstall,
    # -NoInstall: the exe whose update-selftest --no-install runs (a portable build).
    [string] $Exe,
    [switch] $AllowOutsideCi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$UpgradeCode = '{D9D4B1B3-FBF5-48A0-9B6D-2037DF5764DE}' # installer\HomePodCast.wxs, UpdateInstaller.UpgradeCode
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logs = Join-Path $WorkDir 'logs'
$msiexec = Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'
$script:checks = [System.Collections.Generic.List[string]]::new()

function Say([string] $text) { Write-Host "update-e2e: $text" }
function Pass([string] $text) { $script:checks.Add($text); Write-Host "update-e2e: PASS $text" -ForegroundColor Green }
function Fail([string] $text) { throw "update-e2e: FAIL $text" }
function Check([bool] $ok, [string] $text) { if ($ok) { Pass $text } else { Fail $text } }
function Group([string] $name) { Write-Host "::group::$name" }
function EndGroup { Write-Host '::endgroup::' }

# ---------------------------------------------------------------- Windows Installer

function Close-Com { foreach ($o in $args) { if ($null -ne $o -and [Runtime.InteropServices.Marshal]::IsComObject($o)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) } } }

function Get-MsiFileProperty([string] $msi, [string] $name) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $view = $record = $null
    try {
        $db = $installer.OpenDatabase($msi, 0)
        $view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$name'")
        [void]$view.Execute()
        $record = $view.Fetch()
        if ($record) { $record.StringData(1) }
        [void]$view.Close()
    }
    finally { Close-Com $record $view $db $installer }
}

# Every installed product with our UpgradeCode, as Windows Installer reports it (what UpdateInstaller.IsMsiInstall reads).
function Get-InstalledProducts {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        foreach ($code in @($installer.RelatedProducts($UpgradeCode))) { # a StringList: enumerated, it has no Count here
            [pscustomobject]@{
                ProductCode = $code
                Version     = $installer.ProductInfo($code, 'VersionString')
                Location    = [System.IO.Path]::TrimEndingDirectorySeparator($installer.ProductInfo($code, 'InstallLocation'))
            }
        }
    }
    finally { Close-Com $installer }
}

# Add/Remove Programs entries named HomePodCast (Settings → Apps).
function Get-ArpEntries {
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        Get-ChildItem $key -ErrorAction SilentlyContinue | Get-ItemProperty | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -eq 'HomePodCast' }
    }
}

function Invoke-Msiexec([string] $arguments, [string] $what) {
    Say "msiexec $arguments"
    $p = Start-Process -FilePath $msiexec -ArgumentList $arguments -PassThru
    $null = $p.Handle # keeps the exit code readable
    if (-not $p.WaitForExit(600000)) { $p.Kill(); Fail "$what`: msiexec did not finish in 10 minutes" }
    if ($p.ExitCode -notin 0, 3010) { Fail "$what`: msiexec exit code $($p.ExitCode) (log in the update-e2e-logs artifact)" }
    Pass "$what (msiexec exit $($p.ExitCode))"
}

function Assert-Installed([string] $msiVersion, [string] $folder, [string] $what) {
    $products = @(Get-InstalledProducts)
    Say "installed: $(if ($products) { ($products | ForEach-Object { "$($_.Version) in $($_.Location) $($_.ProductCode)" }) -join '; ' } else { 'none' })"
    Check ($products.Count -eq 1) "$what`: exactly one HomePodCast product installed (found $($products.Count))"
    Check ($products[0].Version -eq $msiVersion) "$what`: installed version is $msiVersion (found $($products[0].Version))"
    Check ($products[0].Location -eq $folder) "$what`: install folder is $folder (found $($products[0].Location))"
    $arp = @(Get-ArpEntries)
    Check ($arp.Count -eq 1 -and $arp[0].DisplayVersion -eq $msiVersion) "$what`: one HomePodCast in Add/Remove Programs, version $msiVersion (found $(@($arp | ForEach-Object DisplayVersion) -join ', '))"
    Check (Test-Path -LiteralPath (Join-Path $folder 'HomePodCast.exe')) "$what`: $folder\HomePodCast.exe exists"
    $products[0]
}

# ---------------------------------------------------------------- the app

function Get-UpdateFolders { @(Get-ChildItem -LiteralPath ([System.IO.Path]::GetTempPath()) -Directory -Filter 'HomePodCast-update-*' -ErrorAction SilentlyContinue) }

function Get-AppProcesses { @(Get-CimInstance Win32_Process -Filter "Name = 'HomePodCast.exe'") }

function Stop-App {
    foreach ($p in Get-AppProcesses) {
        Say "stopping HomePodCast pid $($p.ProcessId): $($p.CommandLine)"
        Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
        Wait-Process -Id $p.ProcessId -Timeout 30 -ErrorAction SilentlyContinue
    }
}

$appLog = Join-Path $env:APPDATA 'HomePodCast\homepodcast.log' # AppConfig.Directory, AppConfig.LogFileName

function Get-AppLogSince([long] $offset) {
    if (-not (Test-Path -LiteralPath $appLog)) { return '' }
    $stream = [System.IO.File]::Open($appLog, 'Open', 'Read', 'ReadWrite, Delete')
    try {
        if ($stream.Length -le $offset) { return '' }
        [void]$stream.Seek($offset, 'Begin')
        [System.IO.StreamReader]::new($stream).ReadToEnd()
    }
    finally { $stream.Dispose() }
}

function Get-AppLogLength { if (Test-Path -LiteralPath $appLog) { (Get-Item -LiteralPath $appLog).Length } else { 0 } }

# `HomePodCast.exe update-selftest …`: its output, exit code and pid. Output goes to files: the helper it starts
# inherits the handles and outlives it, so a pipe would not close when it exits.
function Invoke-SelfTest([string] $exe, [string[]] $selfTestArgs, [string] $name) {
    $out = Join-Path $logs "$name.out.txt"
    $err = Join-Path $logs "$name.err.txt"
    Say "$exe update-selftest $($selfTestArgs -join ' ')"
    $p = Start-Process -FilePath $exe -ArgumentList (@('update-selftest') + $selfTestArgs) -RedirectStandardOutput $out -RedirectStandardError $err -PassThru -NoNewWindow
    $null = $p.Handle
    if (-not $p.WaitForExit(180000)) { $p.Kill(); Fail "$name`: update-selftest did not exit in 3 minutes" }
    $text = ((Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) ?? '') + ((Get-Content -LiteralPath $err -Raw -ErrorAction SilentlyContinue) ?? '')
    $text.TrimEnd() -split "`r?`n" | ForEach-Object { Write-Host "    | $_" }
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $text; Pid = $p.Id }
}

# ---------------------------------------------------------------- the fake release list

function Get-FreePort {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start()
    try { $l.LocalEndpoint.Port } finally { $l.Stop() }
}

function Start-Feed([string] $root) {
    $port = Get-FreePort
    $pwsh = (Get-Process -Id $PID).Path
    $out = Join-Path $logs 'feed.out.txt'
    $proc = Start-Process -FilePath $pwsh -ArgumentList "-NoProfile -NonInteractive -File `"$PSScriptRoot\fake-release-feed.ps1`" -Port $port -Root `"$root`"" `
        -RedirectStandardOutput $out -RedirectStandardError (Join-Path $logs 'feed.err.txt') -PassThru -NoNewWindow
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Content -LiteralPath $out -Raw -ErrorAction SilentlyContinue) -match 'listening') {
            return [pscustomobject]@{ Process = $proc; Base = "http://127.0.0.1:$port"; Log = $out }
        }
        if ($proc.HasExited) { break }
        Start-Sleep -Milliseconds 200
    }
    Fail "the fake release feed did not start: $(Get-Content -Raw -LiteralPath (Join-Path $logs 'feed.err.txt') -ErrorAction SilentlyContinue)"
}

function Get-Sha256([string] $file) { (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }

# The release list GitHub's API returns (GET /repos/qiyinxi/HomePodCast/releases), trimmed to what GitHubReleases.Parse reads:
# the new release with its MSI and digest, the old one, and a newer draft that must be ignored.
function Write-Feed([string] $root, [string] $base, [string] $newMsiFile, [string] $sha256) {
    function Release([string] $version, [bool] $draft, [object[]] $assets) {
        [ordered]@{
            tag_name   = "v$version"
            name       = "HomePodCast $version"
            draft      = $draft
            prerelease = $version.Contains('-')
            html_url   = "https://github.com/qiyinxi/HomePodCast/releases/tag/v$version"
            assets     = $assets
        }
    }
    function Asset([string] $name, [long] $size, [string] $digest, [string] $url) {
        [ordered]@{ name = $name; content_type = 'application/x-msdownload'; state = 'uploaded'; size = $size; digest = $digest; browser_download_url = $url }
    }
    $msiName = "HomePodCast-$NewVersion-x64.msi"
    $releases = @(
        (Release '9.9.9' $true @((Asset 'HomePodCast-9.9.9-x64.msi' 1 "sha256:$('0' * 64)" "$base/download/v9.9.9/HomePodCast-9.9.9-x64.msi")))
        (Release $NewVersion $false @(
            (Asset "HomePodCast-$NewVersion-win-x64.zip" 1 "sha256:$('1' * 64)" "$base/download/v$NewVersion/HomePodCast-$NewVersion-win-x64.zip")
            (Asset $msiName (Get-Item -LiteralPath $newMsiFile).Length "sha256:$sha256" "$base/download/v$NewVersion/$msiName")))
        (Release $OldVersion $false @((Asset "HomePodCast-$OldVersion-x64.msi" 1 "sha256:$('2' * 64)" "$base/download/v$OldVersion/HomePodCast-$OldVersion-x64.msi")))
    )
    New-Item -ItemType Directory -Force (Join-Path $root "download\v$NewVersion") | Out-Null
    ConvertTo-Json -InputObject $releases -Depth 6 | Set-Content -LiteralPath (Join-Path $root 'releases') -Encoding utf8NoBOM
    Join-Path $root "download\v$NewVersion\$msiName"
}

# The served MSI: the real one, or the same bytes with a few changed (the SHA-256 in the list stays the real one's).
function Set-ServedMsi([string] $served, [string] $source, [switch] $Corrupt) {
    $bytes = [System.IO.File]::ReadAllBytes($source)
    $middle = [int][Math]::Floor($bytes.Length / 2)
    if ($Corrupt) { for ($i = $middle; $i -lt $middle + 16; $i++) { $bytes[$i] = $bytes[$i] -bxor 0xFF } }
    [System.IO.File]::WriteAllBytes($served, $bytes)
}

function Get-FeedRequests($feed) { @(Get-Content -LiteralPath $feed.Log -ErrorAction SilentlyContinue | Where-Object { $_ -match ' GET ' }) }

# ---------------------------------------------------------------- scenarios

# The installed old app downloads a corrupted MSI: refused, nothing installed, nothing left behind.
function Test-CorruptedDownload($feed, [string] $served, [string] $folder, [string] $oldMsiVersion) {
    Set-ServedMsi $served $NewMsi -Corrupt
    $before = @(Get-FeedRequests $feed).Count
    $r = Invoke-SelfTest (Join-Path $folder 'HomePodCast.exe') @('--feed', "$($feed.Base)/releases", '--channel', $channel) 'corrupted'
    Check ($r.ExitCode -eq 3) "corrupted MSI: update-selftest exits 3 (SHA-256 mismatch; got $($r.ExitCode))"
    Check ($r.Output -match 'does not match GitHub''s') 'corrupted MSI: the app reports the SHA-256 mismatch'
    Check ($r.Output -match [regex]::Escape("newest on the $channelName channel: $NewVersion -> UpdateAvailable")) "corrupted MSI: the check offered $NewVersion"
    $requests = @(Get-FeedRequests $feed | Select-Object -Skip $before)
    Check (@($requests | Where-Object { $_ -match "GET /download/v$([regex]::Escape($NewVersion))/\S+-x64\.msi -> 200" }).Count -eq 1) 'corrupted MSI: it was downloaded from the fake feed'
    Check (@($requests | Where-Object { $_ -match '9\.9\.9' }).Count -eq 0) 'corrupted MSI: the draft release was ignored'
    Check (@(Get-UpdateFolders).Count -eq 0) 'corrupted MSI: no %TEMP%\HomePodCast-update-* folder left'
    Start-Sleep -Seconds 3
    Check (@(Get-AppProcesses).Count -eq 0) 'corrupted MSI: no helper ran and no HomePodCast was started'
    Check (@(Get-CimInstance Win32_Process -Filter "Name = 'msiexec.exe'" | Where-Object { $_.CommandLine -match 'HomePodCast' }).Count -eq 0) 'corrupted MSI: no msiexec for it'
    [void](Assert-Installed $oldMsiVersion $folder 'corrupted MSI: old version still installed')
}

# The installed old app updates itself to the build's MSI through the helper.
function Test-Update($feed, [string] $served, [string] $folder, [string] $newMsiVersion, [string] $name) {
    Set-ServedMsi $served $NewMsi
    $logOffset = Get-AppLogLength
    $since = Get-Date
    $r = Invoke-SelfTest (Join-Path $folder 'HomePodCast.exe') @('--feed', "$($feed.Base)/releases", '--channel', $channel) "$name-selftest"
    Check ($r.ExitCode -eq 0) "$name`: update-selftest started the helper and exited (exit $($r.ExitCode))"
    Check ($r.Output -match [regex]::Escape("newest on the $channelName channel: $NewVersion -> UpdateAvailable")) "$name`: the check offered $NewVersion on the $channelName channel"
    Check ($r.Output -match 'update: MSI install') "$name`: the installed exe counts as an MSI install"
    Check ($r.Output -match 'matches GitHub''s SHA-256') "$name`: the download matched its SHA-256"
    Check ($r.Output -match [regex]::Escape("INSTALLFOLDER=`"$folder`"")) "$name`: the helper installs into $folder"
    if ($r.Output -notmatch '(?m)update: downloading \S+ \(\d+ bytes\) to (.+?)\s*$') { Fail "$name`: no download folder in the output" }
    $updateFolder = $Matches[1]
    Say "update folder: $updateFolder"

    # The helper: waits for the self-test to exit, msiexec /i /passive through Verb runas, result file, app started again.
    $deadline = (Get-Date).AddMinutes(5)
    $app = $null
    $resultAt = $null
    while (-not $app -and (Get-Date) -lt $deadline) {
        foreach ($f in 'msiexec.log', 'result.txt') {
            $src = Join-Path $updateFolder $f
            if (Test-Path -LiteralPath $src) {
                try { Copy-Item -LiteralPath $src (Join-Path $logs "$name-$f") -Force } catch { }
                if ($f -eq 'result.txt' -and -not $resultAt) { $resultAt = Get-Date }
            }
        }
        $app = Get-AppProcesses | Where-Object { $_.CommandLine -like "*--updated-from=$OldVersion*" } | Select-Object -First 1
        if (-not $app) {
            if ($resultAt -and ((Get-Date) - $resultAt).TotalSeconds -gt 60) { break }
            Start-Sleep -Milliseconds 200
        }
    }
    $result = Get-Content -LiteralPath (Join-Path $logs "$name-result.txt") -Raw -ErrorAction SilentlyContinue
    if ($result) { Say "helper result file: $($result.Trim())" }
    if (-not $app) {
        Get-Content -LiteralPath (Join-Path $logs "$name-msiexec.log") -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    msiexec | $_" }
        Fail "$name`: HomePodCast was not started again with --updated-from=$OldVersion within 5 minutes"
    }
    Say "relaunched: pid $($app.ProcessId) $($app.CommandLine)"
    Check ($app.ExecutablePath -eq (Join-Path $folder 'HomePodCast.exe')) "$name`: the relaunched process is $folder\HomePodCast.exe"
    Check ($app.CommandLine -match "--updated-from=$([regex]::Escape($OldVersion))(\s|$)" -and $app.CommandLine -match '--tray') "$name`: started with --updated-from=$OldVersion --tray"

    # The new app reads the result, says so, and removes the update folder (UpdatePackage.CleanUp).
    $expected = "update: updated from $OldVersion to $NewVersion (msiexec: 0)"
    $deadline = (Get-Date).AddSeconds(90)
    do {
        $log = Get-AppLogSince $logOffset
        $done = $log.Contains($expected) -and -not (Test-Path -LiteralPath $updateFolder)
        if (-not $done) { Start-Sleep -Milliseconds 500 }
    } while (-not $done -and (Get-Date) -lt $deadline)
    $log.TrimEnd() -split "`r?`n" | Where-Object { $_ -match 'starting|update' } | ForEach-Object { Write-Host "    app | $_" }
    Check ($log.Contains($expected)) "$name`: the new app logged `"$expected`""
    Check ($log -match "update: removed $([regex]::Escape($updateFolder))") "$name`: the new app removed the update folder"
    Check (-not (Test-Path -LiteralPath $updateFolder) -and @(Get-UpdateFolders).Count -eq 0) "$name`: no %TEMP%\HomePodCast-update-* folder left"

    $installed = Assert-Installed $newMsiVersion $folder "$name`: after the update"
    $exeVersion = (Get-Item -LiteralPath (Join-Path $folder 'HomePodCast.exe')).VersionInfo.ProductVersion ?? ''
    Check ($exeVersion -eq $NewVersion -or $exeVersion.StartsWith("$NewVersion+")) "$name`: the installed exe is $NewVersion ($exeVersion)"

    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'MsiInstaller'; StartTime = $since } -ErrorAction SilentlyContinue | Sort-Object TimeCreated)
    $events | ForEach-Object { Write-Host "    event $($_.Id) | $(($_.Message -split "`r?`n")[0])" }
    Check (@($events | Where-Object { $_.Id -eq 1033 -and $_.Message -match "Product Version: $([regex]::Escape($newMsiVersion))\." -and $_.Message -match 'status: 0\.' }).Count -ge 1) "$name`: Windows Installer logged the install of $newMsiVersion with status 0 (event 1033)"
    $msiLog = Join-Path $logs "$name-msiexec.log"
    if (Test-Path -LiteralPath $msiLog) {
        $tail = Get-Content -LiteralPath $msiLog -Tail 400 | Where-Object { $_ -match 'Installation (operation )?completed successfully|success or error status|MainEngineThread is returning' }
        $tail | ForEach-Object { Write-Host "    msiexec | $_" }
    }
    else { Say 'the msiexec log was removed with the update folder before it could be copied (the event log above has the result)' }

    Stop-App
    $installed
}

# ---------------------------------------------------------------- main

New-Item -ItemType Directory -Force $WorkDir, $logs | Out-Null
$NewMsi = (Resolve-Path -LiteralPath $NewMsi).Path
$channel = if ($NewVersion.Contains('-')) { 'beta' } else { 'stable' }
$channelName = if ($channel -eq 'beta') { 'Beta' } else { 'Stable' }
$feedRoot = Join-Path $WorkDir 'feed'
Remove-Item -LiteralPath $feedRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $feedRoot | Out-Null
$sha256 = Get-Sha256 $NewMsi
$feed = $null

if ($NoInstall) {
    # Nothing is installed: the fake feed and `update-selftest --no-install` of a portable build.
    if (-not $Exe) { throw '-NoInstall needs -Exe (a portable HomePodCast.exe to run update-selftest --no-install with)' }
    $Exe = (Resolve-Path -LiteralPath $Exe).Path
    try {
        $feed = Start-Feed $feedRoot
        $served = Write-Feed $feedRoot $feed.Base $NewMsi $sha256
        Set-ServedMsi $served $NewMsi
        $r = Invoke-SelfTest $Exe @('--feed', "$($feed.Base)/releases", '--channel', $channel, '--no-install') 'no-install-good'
        Check ($r.ExitCode -eq 0 -and $r.Output -match 'matches GitHub''s SHA-256' -and $r.Output -match 'removed again, nothing installed') 'no-install: downloaded, matched, removed'
        Set-ServedMsi $served $NewMsi -Corrupt
        $r = Invoke-SelfTest $Exe @('--feed', "$($feed.Base)/releases", '--channel', $channel, '--no-install') 'no-install-corrupted'
        Check ($r.ExitCode -eq 3 -and $r.Output -match 'does not match') 'no-install: a corrupted MSI is refused'
        $r = Invoke-SelfTest $Exe @('--feed', 'https://api.github.com/repos/qiyinxi/HomePodCast/releases?per_page=20', '--no-install') 'no-install-github'
        Check ($r.ExitCode -eq 1 -and $r.Output -match 'must be an http://127\.0\.0\.1 or http://localhost URL') 'no-install: a feed that is not on 127.0.0.1 is refused'
        Check (@(Get-UpdateFolders).Count -eq 0) 'no-install: no %TEMP%\HomePodCast-update-* folder left'
        Get-Content -LiteralPath $feed.Log | ForEach-Object { Write-Host "    feed | $_" }
    }
    finally { if ($feed) { Stop-Process -Id $feed.Process.Id -Force -ErrorAction SilentlyContinue } }
    Say "all $($checks.Count) checks passed (nothing installed)"
    return
}

if ($env:GITHUB_ACTIONS -ne 'true' -and -not $AllowOutsideCi) {
    throw 'update-e2e.ps1 installs and uninstalls HomePodCast MSIs, so it runs on a CI runner only (GITHUB_ACTIONS=true). On a throwaway VM pass -AllowOutsideCi; for a check that installs nothing use -NoInstall.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'update-e2e.ps1 needs an elevated PowerShell (msiexec /qn of a per-machine MSI)' }
if (@(Get-InstalledProducts).Count) { throw "HomePodCast is installed on this machine ($((Get-InstalledProducts | ForEach-Object { "$($_.Version) in $($_.Location)" }) -join '; ')): update-e2e.ps1 would replace and uninstall it. Run it on a clean machine." }
if (Get-AppProcesses) { throw 'HomePodCast is running: update-e2e.ps1 runs on a clean machine only.' }

$uac = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue
Say ("UAC: EnableLUA=$($uac.EnableLUA) ConsentPromptBehaviorAdmin=$($uac.ConsentPromptBehaviorAdmin) PromptOnSecureDesktop=$($uac.PromptOnSecureDesktop); " +
     'the helper''s Verb runas elevates without a dialog here. The UAC dialog itself is the part only a person sees.')

try {
    Group "Build the old MSI ($OldVersion) from this source"
    $oldPublish = Join-Path $WorkDir 'old\HomePodCast'
    $oldOut = Join-Path $WorkDir 'old'
    dotnet publish (Join-Path $repo 'src\HomePodCast.csproj') -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true "-p:Version=$OldVersion" -o $oldPublish | Out-Host
    if ($LASTEXITCODE) { throw "dotnet publish ($OldVersion) failed" }
    & (Join-Path $repo 'installer\build.ps1') -Version $OldVersion -Build 1 -PublishDir $oldPublish -OutDir $oldOut | Out-Host
    $oldMsi = Join-Path $oldOut "HomePodCast-$OldVersion-x64.msi"
    if (-not (Test-Path -LiteralPath $oldMsi)) { throw "$oldMsi was not built" }
    EndGroup

    $oldMsiVersion = Get-MsiFileProperty $oldMsi 'ProductVersion'
    $newMsiVersion = Get-MsiFileProperty $NewMsi 'ProductVersion'
    Say "old: $OldVersion (MSI $oldMsiVersion) $oldMsi"
    Say "new: $NewVersion (MSI $newMsiVersion) $NewMsi, SHA-256 $sha256"
    Check ([version]$oldMsiVersion -lt [version]$newMsiVersion) "the old MSI version $oldMsiVersion is below the new one $newMsiVersion (a major upgrade, not a downgrade)"
    Check ((Get-MsiFileProperty $oldMsi 'UpgradeCode') -eq $UpgradeCode -and (Get-MsiFileProperty $NewMsi 'UpgradeCode') -eq $UpgradeCode) 'both MSIs have the UpgradeCode the app looks for'

    $feed = Start-Feed $feedRoot
    $served = Write-Feed $feedRoot $feed.Base $NewMsi $sha256
    Say "fake release list: $($feed.Base)/releases (channel $channel)"

    # 1. The default folder: a corrupted download first, then the update.
    Group 'Default folder: install the old MSI'
    Invoke-Msiexec "/i `"$oldMsi`" /qn /norestart /l*v `"$(Join-Path $logs 'default-install-old.log')`"" 'default folder: old MSI installed'
    $defaultFolder = Join-Path ${env:ProgramFiles} 'HomePodCast'
    [void](Assert-Installed $oldMsiVersion $defaultFolder 'default folder: before the update')
    EndGroup
    Group 'Default folder: a corrupted download is refused'
    Test-CorruptedDownload $feed $served $defaultFolder $oldMsiVersion
    EndGroup
    Group 'Default folder: update through update-selftest and the helper'
    $installed = Test-Update $feed $served $defaultFolder $newMsiVersion 'default folder'
    Invoke-Msiexec "/x $($installed.ProductCode) /qn /norestart /l*v `"$(Join-Path $logs 'default-uninstall.log')`"" 'default folder: uninstalled'
    Check (@(Get-InstalledProducts).Count -eq 0) 'default folder: nothing installed after the uninstall'
    EndGroup

    # 2. A custom folder (with spaces): the update must stay there, not move to Program Files.
    Group "Custom folder ($CustomFolder): install the old MSI"
    Invoke-Msiexec "/i `"$oldMsi`" /qn /norestart /l*v `"$(Join-Path $logs 'custom-install-old.log')`" INSTALLFOLDER=`"$CustomFolder`"" 'custom folder: old MSI installed'
    [void](Assert-Installed $oldMsiVersion $CustomFolder 'custom folder: before the update')
    EndGroup
    Group 'Custom folder: update through update-selftest and the helper'
    $installed = Test-Update $feed $served $CustomFolder $newMsiVersion 'custom folder'
    Check (-not (Test-Path -LiteralPath (Join-Path $defaultFolder 'HomePodCast.exe'))) "custom folder: nothing was installed into $defaultFolder"
    Invoke-Msiexec "/x $($installed.ProductCode) /qn /norestart /l*v `"$(Join-Path $logs 'custom-uninstall.log')`"" 'custom folder: uninstalled'
    Check (@(Get-InstalledProducts).Count -eq 0) 'custom folder: nothing installed after the uninstall'
    EndGroup

    Group 'Fake feed requests'
    Get-Content -LiteralPath $feed.Log | ForEach-Object { Write-Host "    feed | $_" }
    Check (@(Get-FeedRequests $feed | Where-Object { $_ -notmatch "\[HomePodCast/$([regex]::Escape($OldVersion))\]" }).Count -eq 0) 'every request came from the old app (User-Agent HomePodCast/<old version>)'
    EndGroup
}
finally {
    if ($feed) { Stop-Process -Id $feed.Process.Id -Force -ErrorAction SilentlyContinue }
    Stop-App
    if (Test-Path -LiteralPath $appLog) { Copy-Item -LiteralPath $appLog (Join-Path $logs 'homepodcast.log') -Force -ErrorAction SilentlyContinue }
}

Say "all $($checks.Count) checks passed: $OldVersion -> $NewVersion updated in place in the default folder and in `"$CustomFolder`"; a corrupted download was refused"
