<#
.SYNOPSIS
  End-to-end test of the HomePodCast MSI: the msi-e2e job of .github/workflows/build.yml.

.DESCRIPTION
  Automates section 七「安装包（MSI）」 of docs/device-test-checklist.zh-CN.md on a throwaway CI runner:
    1. silent install into the default folder: the files, one Start menu shortcut, one entry in Programs and Features,
       and one firewall rule for exactly that exe, matching the rule the app adds itself (Firewall.RuleScript: inbound,
       allow, any protocol, Private profile, LocalSubnet). The installed exe's own startup check ("HomePodCast.exe
       firewall", FirewallCommand.cs) must find it and decide on no firewall prompt;
    2. the same in a custom folder (INSTALLFOLDER, with a space and Chinese characters in the path);
    3. after each uninstall: the files, the shortcut, the firewall rule and the Programs entry are gone, and the settings
       seeded in %APPDATA%\HomePodCast (config.json, the log) are untouched;
    4. upgrades from published releases (gh release download; GH_TOKEN): afterwards one HomePodCast in Programs and
       Features, the new version, in the same folder, and still one correct firewall rule. The old and new installs use
       different wizard languages (TRANSFORMS=:2052), which MajorUpgrade IgnoreLanguage must not mind. A release newer
       than this build can't be upgraded from (branch and PR builds are MSI version 0.2.0.<run>): then this MSI must
       refuse the downgrade with its message and leave the release installed;
    5. wizard languages: every culture of installer/build.ps1 is an embedded language transform, each one has its own
       finish-page text from installer/i18n, and the zh-CN one shows the wizard in Chinese (「启动 HomePodCast」 on the
       finish page), with the license page in English as expected.
  Every check prints "ok  <what>"; the first failure throws (exit code 1) after printing the msiexec log's error lines.

  It installs and uninstalls HomePodCast for all users, so it runs only on a CI runner (GITHUB_ACTIONS=true) on which
  HomePodCast is not installed. -StaticOnly runs check 5 alone: it only reads the MSI and runs anywhere.

.EXAMPLE
  ./tools/ci/msi-e2e.ps1 -Msi publish/HomePodCast-0.2.0-local.1-x64.msi -StaticOnly
#>
param(
    [Parameter(Mandatory)] [string] $Msi,
    # Releases to upgrade from; 'latest' is the newest published release (prereleases included).
    [string[]] $OldReleases = @('v0.2.0', 'v0.2.1', 'v0.2.2', 'latest'),
    [string] $Repo = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'qiyinxi/HomePodCast' }),
    [string] $WorkDir = (Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'msi-e2e'),
    [switch] $StaticOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.Encoding]::UTF8 # the Chinese texts in the CI log

$RepoDir = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$Msi = (Resolve-Path $Msi).Path
$DefaultFolder = Join-Path $env:ProgramFiles 'HomePodCast'
$CustomFolder = 'C:\Custom Apps\HomePodCast 测试'
$RuleName = 'HomePodCast (installer)'   # installer/HomePodCast.wxs
$AppDataDir = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'HomePodCast'
$LogDir = Join-Path $WorkDir 'logs'
New-Item -ItemType Directory -Force $WorkDir, $LogDir | Out-Null

$script:Passed = [System.Collections.Generic.List[string]]::new()
$script:Section = ''

function Write-Section([string] $title) {
    $script:Section = $title
    Write-Host ''
    Write-Host "=== $title ==="
}

function Check([bool] $condition, [string] $what) {
    if (-not $condition) { throw "FAILED [$script:Section] $what" }
    Write-Host "  ok  $what"
    $script:Passed.Add("$script:Section | $what")
}

function Close-Com {
    foreach ($o in $args) {
        if ($null -ne $o -and [Runtime.InteropServices.Marshal]::IsComObject($o)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) }
    }
}

$Installer = New-Object -ComObject WindowsInstaller.Installer

# ---------------------------------------------------------------------------------------------------------------------
# Reading MSI packages (Windows Installer COM, read-only on a copy)

# Opens a copy of $path, with the embedded language transform :<lcid> applied if given, the way Windows Installer applies
# it at install time (error conditions 0x3F, what wix msi transform -t language writes into the transform). Never committed.
function Open-MsiCopy([string] $path, [int] $lcid = 0) {
    $copy = Join-Path $WorkDir ("db-$([guid]::NewGuid().ToString('N')).msi")
    Copy-Item $path $copy
    $db = $Installer.OpenDatabase($copy, 1) # msiOpenDatabaseModeTransact
    if ($lcid) { [void]$db.ApplyTransform(":$lcid", 0x3F) }
    $db
}

# Rows of a query as objects with the given column names (Record.StringData; no binary columns).
function Get-MsiRows($db, [string] $sql, [string[]] $columns) {
    $view = $db.OpenView($sql)
    try {
        [void]$view.Execute()
        while ($true) {
            $record = $view.Fetch()
            if ($null -eq $record) { break }
            $row = [ordered]@{}
            for ($k = 0; $k -lt $columns.Count; $k++) { $row[$columns[$k]] = $record.StringData($k + 1) }
            Close-Com $record
            [pscustomobject]$row
        }
        [void]$view.Close()
    }
    finally {
        Close-Com $view
    }
}

function Get-MsiProperties($db) {
    $props = @{}
    foreach ($row in Get-MsiRows $db 'SELECT `Property`, `Value` FROM `Property`' 'Property', 'Value') { $props[$row.Property] = $row.Value }
    $props
}

function Get-MsiInfo([string] $path) {
    $db = Open-MsiCopy $path
    $summary = $null
    try {
        $props = Get-MsiProperties $db
        $summary = $db.SummaryInformation(0)
        $template = [string]$summary.Property(7)  # PID_TEMPLATE "x64;1033,2052,..."
        $subject = [string]$summary.Property(3)   # "HomePodCast <CI version> (x64)" (SummaryInformation Description)
        [pscustomobject]@{
            Path           = $path
            ProductCode    = $props['ProductCode']
            UpgradeCode    = $props['UpgradeCode']
            ProductVersion = $props['ProductVersion']
            Version3       = Get-Version3 $props['ProductVersion']
            Languages      = @(($template -split ';')[1] -split ',' | ForEach-Object { [int]$_ })
            Template       = $template
            DisplayVersion = if ($subject -match '^HomePodCast (\S+) \(x64\)$') { $Matches[1] } else { $null }
        }
    }
    finally {
        Close-Com $summary $db
    }
}

# Windows Installer compares the first three fields of a ProductVersion.
function Get-Version3([string] $v) {
    $p = $v.Split('.')
    [version]::new([int]$p[0], [int]$p[1], [int]$p[2])
}

# installer/build.ps1's $Cultures: culture -> LCID, base culture first.
function Get-Cultures {
    $cultures = [ordered]@{}
    $source = Get-Content (Join-Path $RepoDir 'installer/build.ps1') -Raw
    foreach ($m in [regex]::Matches($source, "(?m)^\s*'([a-z]{2}-[A-Z]{2})'\s*=\s*(\d+)\s*$")) { $cultures[$m.Groups[1].Value] = [int]$m.Groups[2].Value }
    if ($cultures.Count -lt 2) { throw "could not read the cultures from installer/build.ps1" }
    $cultures
}

# Our own installer strings of a culture (installer/i18n/<culture>.wxl).
function Get-WxlStrings([string] $culture) {
    [xml] $loc = Get-Content (Join-Path $RepoDir "installer/i18n/$culture.wxl") -Raw -Encoding utf8
    $strings = @{}
    foreach ($s in $loc.WixLocalization.String) { $strings[$s.GetAttribute('Id')] = $s.GetAttribute('Value') }
    $strings
}

$Cultures = Get-Cultures
$CultureOf = @{}
foreach ($c in $Cultures.Keys) { $CultureOf[$Cultures[$c]] = $c }

# ---------------------------------------------------------------------------------------------------------------------
# 5. Wizard languages (static)

# The wizard dialogs a user goes through on a first install, and texts that are not translated on purpose.
$WizardDialogs = 'WelcomeDlg', 'LicenseAgreementDlg', 'InstallDirDlg', 'VerifyReadyDlg', 'ProgressDlg', 'ExitDialog'

function Get-WizardTexts($db) {
    $texts = @{}
    foreach ($row in Get-MsiRows $db 'SELECT `Dialog_`, `Control`, `Type`, `Text` FROM `Control`' 'Dialog', 'Control', 'Type', 'Text') {
        if ($row.Dialog -in $WizardDialogs -and $row.Type -in 'Text', 'PushButton', 'CheckBox', 'ScrollableText') { $texts["$($row.Dialog).$($row.Control)"] = $row.Text }
    }
    foreach ($row in Get-MsiRows $db 'SELECT `Dialog`, `Title` FROM `Dialog`' 'Dialog', 'Title') {
        if ($row.Dialog -in $WizardDialogs) { $texts["$($row.Dialog) (title bar)"] = $row.Title }
    }
    $texts
}

# Text the user reads: [ProductName] filled in, without other [Property] references and {\TextStyle} prefixes.
function Get-VisibleText([string] $text) {
    ($text -replace '\[ProductName\]', 'HomePodCast' -replace '\[[^\]]*\]', '' -replace '\{[^}]*\}', '' -replace '  +', ' ').Trim()
}

function Test-Languages([pscustomobject] $info) {
    Write-Section '5. Wizard languages (MSI tables)'
    $base = @($Cultures.Keys)[0]
    $expectedTemplate = "x64;$(@($Cultures.Values) -join ',')"
    Check ($info.Template -eq $expectedTemplate) "language list (summary Template) is '$($info.Template)': every culture of installer/build.ps1"

    $db = Open-MsiCopy $info.Path
    try {
        $storages = @(Get-MsiRows $db 'SELECT `Name` FROM `_Storages`' 'Name' | ForEach-Object Name | Sort-Object)
        $expectedStorages = @($Cultures.Values | Select-Object -Skip 1 | ForEach-Object { "$_" } | Sort-Object)
        Check (($storages -join ',') -eq ($expectedStorages -join ',')) "embedded language transforms: $($storages -join ', ')"
        $props = Get-MsiProperties $db
        $baseStrings = Get-WxlStrings $base
        Check ($props['ProductLanguage'] -eq "$($Cultures[$base])" -and $props['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'] -ceq $baseStrings['LaunchApp']) "$base base package: ProductLanguage $($props['ProductLanguage']), finish checkbox '$($props['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'])'"
        Check ($props['WIXUI_INSTALLDIR'] -eq 'INSTALLFOLDER') 'the wizard has the folder page for INSTALLFOLDER (WIXUI_INSTALLDIR)'
        $folder = @(Get-MsiRows $db "SELECT ``Directory_Parent``, ``DefaultDir`` FROM ``Directory`` WHERE ``Directory`` = 'INSTALLFOLDER'" 'Parent', 'DefaultDir')
        Check ($folder.Count -eq 1 -and $folder[0].Parent -eq 'ProgramFiles64Folder' -and ($folder[0].DefaultDir -split '\|')[-1] -eq 'HomePodCast') 'default folder is [ProgramFiles64Folder]HomePodCast'
        $baseTexts = Get-WizardTexts $db
    }
    finally {
        Close-Com $db
    }

    foreach ($culture in @($Cultures.Keys | Select-Object -Skip 1)) {
        $lcid = $Cultures[$culture]
        $strings = Get-WxlStrings $culture
        $db = Open-MsiCopy $info.Path $lcid
        try {
            $props = Get-MsiProperties $db
            $texts = Get-WizardTexts $db
        }
        finally {
            Close-Com $db
        }
        $welcome = $texts['WelcomeDlg.Title']
        Check ($props['ProductLanguage'] -eq "$lcid" -and $props['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'] -ceq $strings['LaunchApp'] -and $welcome -cne $baseTexts['WelcomeDlg.Title']) "transform :$lcid ($culture): ProductLanguage $lcid, finish checkbox '$($strings['LaunchApp'])', welcome title '$(Get-VisibleText $welcome)'"
    }

    # zh-CN in detail: the checklist's 「中文 Windows 上安装向导是中文（包括完成页的「启动 HomePodCast」）；许可协议页是英文原文」.
    $zh = Get-WxlStrings 'zh-CN'
    $db = Open-MsiCopy $info.Path 2052
    try {
        $props = Get-MsiProperties $db
        $texts = Get-WizardTexts $db
        $launch = @(Get-MsiRows $db 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 'Condition', 'Description')
        $shortcut = @(Get-MsiRows $db 'SELECT `Description` FROM `Shortcut`' 'Description')
        $rule = @(Get-MsiRows $db 'SELECT `Name`, `Description`, `RemoteAddresses`, `Profile`, `Program` FROM `Wix5FirewallException`' 'Name', 'Description', 'RemoteAddresses', 'Profile', 'Program')
    }
    finally {
        Close-Com $db
    }
    Check ($props['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'] -ceq '启动 HomePodCast' -and $zh['LaunchApp'] -ceq '启动 HomePodCast') "zh-CN finish page checkbox: '$($props['WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'])'"
    Check (($launch | Where-Object Condition -eq 'NOT WIX_DOWNGRADE_DETECTED').Description -ceq $zh['DowngradeError']) "zh-CN downgrade message: '$($zh['DowngradeError'])'"
    Check (@($launch | Where-Object Description -ceq $zh['DotNetMissing']).Count -eq 1) 'zh-CN missing .NET 10 message from zh-CN.wxl'
    Check ($shortcut.Count -eq 1 -and $shortcut[0].Description -ceq $zh['ShortcutDescription']) "zh-CN Start menu shortcut description: '$($zh['ShortcutDescription'])'"
    Check ($rule.Count -eq 1 -and $rule[0].Name -eq $RuleName -and $rule[0].Description -ceq $zh['FirewallRuleDescription']) "zh-CN firewall rule '$RuleName' with the zh-CN description"

    $cjk = '[\u3400-\u9FFF\uFF00-\uFFEF\u3000-\u303F]'
    $translated = 0
    foreach ($key in @($baseTexts.Keys | Sort-Object)) {
        $english = $baseTexts[$key] -replace '\[[^\]]*\]', '' -replace '\{[^}]*\}', ''
        if ($english -notmatch '[A-Za-z]{2}') { continue } # bitmaps, lines, [Property]-only texts
        $chinese = $texts[$key]
        if ($null -eq $chinese) { throw "FAILED [$script:Section] zh-CN has no wizard text $key" }
        if ($key -eq 'LicenseAgreementDlg.LicenseText') {
            Check ($chinese -ceq $baseTexts[$key] -and $chinese -match 'GNU GENERAL PUBLIC LICENSE') 'zh-CN license page: the GPL-3.0 in English (expected: the official text)'
            continue
        }
        if ((Get-VisibleText $chinese) -notmatch $cjk) { throw "FAILED [$script:Section] zh-CN wizard text $key is not Chinese: '$chinese' (en-US: '$($baseTexts[$key])')" }
        $translated++
    }
    Check ($translated -ge 20) "zh-CN wizard: all $translated texts of $($WizardDialogs -join ', ') are Chinese, e.g. '$(Get-VisibleText $texts['WelcomeDlg.Title'])', '$(Get-VisibleText $texts['InstallDirDlg.Title'])', '$($texts['VerifyReadyDlg.Install'])', '$(Get-VisibleText $texts['ExitDialog.Title'])', '$($texts['ExitDialog.Finish'])'"
}

$New = Get-MsiInfo $Msi
Write-Host "New MSI: $(Split-Path $Msi -Leaf), version $($New.DisplayVersion) (MSI $($New.ProductVersion)), product code $($New.ProductCode), upgrade code $($New.UpgradeCode)"
if (-not $New.DisplayVersion) { throw "can't read the CI version from the MSI's summary information" }

if ($StaticOnly) {
    Test-Languages $New
    Write-Host ''
    Write-Host "All $($script:Passed.Count) checks passed (static only)."
    exit 0
}

# ---------------------------------------------------------------------------------------------------------------------
# Installing for real: only on a CI runner

if ($env:GITHUB_ACTIONS -ne 'true') {
    throw 'This script installs and uninstalls HomePodCast for all users: it runs only on a CI runner (GITHUB_ACTIONS=true). Use -StaticOnly to check an MSI without installing it.'
}
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'msiexec /qn needs an elevated (administrator) session'
}

$CommonPrograms = [Environment]::GetFolderPath('CommonPrograms')
$Shell = New-Object -ComObject WScript.Shell

function Get-ArpEntries {
    $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    foreach ($key in $keys) {
        if (-not (Test-Path $key)) { continue }
        foreach ($sub in Get-ChildItem $key) {
            $p = Get-ItemProperty $sub.PSPath
            $name = $p.PSObject.Properties['DisplayName']
            if ($name -and "$($name.Value)" -like '*HomePodCast*') {
                [pscustomobject]@{
                    Key             = $sub.PSChildName
                    DisplayName     = $name.Value
                    DisplayVersion  = $p.PSObject.Properties['DisplayVersion']?.Value
                    Publisher       = $p.PSObject.Properties['Publisher']?.Value
                    InstallLocation = $p.PSObject.Properties['InstallLocation']?.Value
                    WindowsInstaller = $p.PSObject.Properties['WindowsInstaller']?.Value
                }
            }
        }
    }
}

function Get-RelatedProducts([string] $upgradeCode) {
    $list = $Installer.RelatedProducts($upgradeCode)
    try { for ($k = 0; $k -lt $list.Count; $k++) { $list.Item($k) } }
    finally { Close-Com $list }
}

function Get-FirewallRules {
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    foreach ($r in $policy.Rules) {
        [pscustomobject]@{
            Name            = $r.Name
            ApplicationName = $r.ApplicationName
            Direction       = $r.Direction
            Action          = $r.Action
            Enabled         = $r.Enabled
            Profiles        = $r.Profiles
            Protocol        = $r.Protocol
            RemoteAddresses = $r.RemoteAddresses
            Description     = $r.Description
        }
    }
}

# Shortcuts named HomePodCast or pointing at a HomePodCast.exe, in the Start menu and on the desktop, all users and this one.
function Get-Shortcuts {
    $dirs = 'CommonPrograms', 'Programs', 'CommonDesktopDirectory', 'DesktopDirectory' | ForEach-Object { [Environment]::GetFolderPath($_) }
    foreach ($dir in $dirs) {
        if (-not $dir -or -not (Test-Path $dir)) { continue }
        foreach ($file in Get-ChildItem $dir -Recurse -Filter *.lnk -ErrorAction SilentlyContinue) {
            $link = $Shell.CreateShortcut($file.FullName)
            if ($file.BaseName -like '*HomePodCast*' -or $link.TargetPath -like '*HomePodCast.exe') {
                [pscustomobject]@{ Path = $file.FullName; Target = $link.TargetPath; WorkingDirectory = $link.WorkingDirectory; Description = $link.Description }
            }
            Close-Com $link
        }
    }
}

# The Start menu shortcut component's key path (installer/HomePodCast.wxs), or $null.
function Get-ShortcutMarker {
    try { Get-ItemPropertyValue 'HKCU:\Software\HomePodCast\Installer' 'StartMenuShortcut' -ErrorAction Stop }
    catch { $null }
}

function Get-AppDataSnapshot {
    if (-not (Test-Path $AppDataDir)) { return '(none)' }
    @(Get-ChildItem $AppDataDir -Recurse -File | Sort-Object FullName | ForEach-Object {
        "$($_.FullName.Substring($AppDataDir.Length)) $($_.Length) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash) $($_.LastWriteTimeUtc.ToString('o'))"
    }) -join "`n"
}

# Lines around the failure in a verbose msiexec log.
function Show-MsiLog([string] $log) {
    if (-not (Test-Path $log)) { return }
    $lines = Get-Content $log
    $hit = $lines | Select-String -Pattern 'Return value 3' | Select-Object -First 1
    $from = if ($hit) { [Math]::Max(0, $hit.LineNumber - 40) } else { [Math]::Max(0, $lines.Count - 40) }
    Write-Host "--- $log (lines $($from + 1)-$([Math]::Min($lines.Count, $from + 45)))"
    $lines[$from..([Math]::Min($lines.Count, $from + 45) - 1)] | ForEach-Object { Write-Host "    $_" }
    $lines | Select-String -Pattern 'Error \d{4}\.|error code|Product: .*--' | Select-Object -First 15 | ForEach-Object { Write-Host "    $($_.Line)" }
}

$script:LogNumber = 0
function Invoke-Msiexec([string] $what, [string] $arguments, [int[]] $expect = @(0)) {
    $script:LogNumber++
    $log = Join-Path $LogDir ('{0:00}-{1}.log' -f $script:LogNumber, ($what -replace '[^A-Za-z0-9.]+', '-'))
    $commandLine = "$arguments /qn /norestart /l*v `"$log`""
    for ($attempt = 1; ; $attempt++) {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $psi = [Diagnostics.ProcessStartInfo]::new((Join-Path ([Environment]::SystemDirectory) 'msiexec.exe'), $commandLine)
        $psi.UseShellExecute = $false
        $p = [Diagnostics.Process]::Start($psi)
        if (-not $p.WaitForExit(600000)) { $p.Kill(); throw "msiexec ($what) still running after 10 minutes (log $log)" }
        $code = $p.ExitCode
        Write-Host "  msiexec $commandLine -> exit code $code in $([int]$sw.Elapsed.TotalSeconds) s"
        # 1618: another installation is in progress (the previous one's service-side cleanup); try again shortly.
        if ($code -ne 1618 -or $attempt -ge 5) { break }
        Start-Sleep -Seconds 10
    }
    if ($code -notin $expect) {
        Show-MsiLog $log
        throw "FAILED [$script:Section] msiexec ($what) exited with $code, expected $($expect -join ' or ')"
    }
    [pscustomobject]@{ Code = $code; Log = $log }
}

# The installed exe's own startup check (FirewallCommand.cs): what the app decides when it starts.
function Invoke-AppFirewallCheck([string] $exe) {
    $psi = [Diagnostics.ProcessStartInfo]::new($exe, 'firewall')
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw "FAILED [$script:Section] $exe firewall still running after 60 s" }
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = ($out.Result + $err.Result).Trim() }
}

# Everything an install leaves: one product, its files, one shortcut, one firewall rule.
# -Full: the new MSI's own checks too (its version, its strings, the app's startup check, which old versions lack).
function Assert-Installed([pscustomobject] $info, [string] $folder, [int] $language, [string[]] $notIn = @(), [switch] $Full) {
    $folder = $folder.TrimEnd('\')
    $exe = Join-Path $folder 'HomePodCast.exe'
    $culture = $CultureOf[$language]
    $strings = Get-WxlStrings $culture

    $arp = @(Get-ArpEntries)
    Check ($arp.Count -eq 1) "Programs and Features: exactly one HomePodCast entry ($(@($arp | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion) $($_.Key)" }) -join '; '))"
    Check ($arp[0].Key -eq $info.ProductCode -and $arp[0].DisplayVersion -eq $info.ProductVersion -and $arp[0].Publisher -eq 'HomePodCast' -and $arp[0].WindowsInstaller -eq 1) "the entry is product $($info.ProductCode), version $($info.ProductVersion), publisher HomePodCast"
    Check ("$($arp[0].InstallLocation)".TrimEnd('\') -eq $folder) "its install location is $folder"
    $related = @(Get-RelatedProducts $info.UpgradeCode)
    Check ($related.Count -eq 1 -and $related[0] -eq $info.ProductCode) "Windows Installer knows one product with the upgrade code: $($related -join ', ')"
    Check ($Installer.ProductInfo($info.ProductCode, 'AssignmentType') -eq '1') 'installed per-machine (all users)'
    Check ($Installer.ProductInfo($info.ProductCode, 'Language') -eq "$language") "installed in $culture ($language)"

    Check ((Test-Path $exe) -and (Test-Path (Join-Path $folder 'LICENSE')) -and (Test-Path (Join-Path $folder 'README.md'))) "files in $folder (HomePodCast.exe, LICENSE, README.md)"
    if ($Full) {
        $productVersion = (Get-Item $exe).VersionInfo.ProductVersion
        Check ($productVersion -eq $info.DisplayVersion -or $productVersion.StartsWith("$($info.DisplayVersion)+")) "HomePodCast.exe is the new build ($productVersion)"
    }
    foreach ($other in $notIn) { Check (-not (Test-Path (Join-Path $other 'HomePodCast.exe'))) "no HomePodCast.exe in $other" }

    $links = @(Get-Shortcuts)
    Check ($links.Count -eq 1) "exactly one shortcut: $(@($links | ForEach-Object Path) -join ', ')"
    Check ($links[0].Path -eq (Join-Path $CommonPrograms 'HomePodCast.lnk') -and $links[0].Target -eq $exe -and $links[0].WorkingDirectory.TrimEnd('\') -eq $folder) "Start menu (all users) HomePodCast.lnk -> $exe, working folder $folder; no desktop shortcut"
    if ($Full) { Check ($links[0].Description -ceq $strings['ShortcutDescription']) "shortcut description in $culture`: '$($links[0].Description)'" }

    $rules = @(Get-FirewallRules)
    $forExe = @($rules | Where-Object { $_.ApplicationName -and $_.ApplicationName -ieq $exe })
    $named = @($rules | Where-Object Name -eq $RuleName)
    Check ($forExe.Count -eq 1 -and $named.Count -eq 1) "exactly one firewall rule for $exe and one named '$RuleName' ($($forExe.Count) and $($named.Count))"
    $r = $forExe[0]
    # Firewall.RuleScript: New-NetFirewallRule -Direction Inbound -Action Allow -Profile Private -RemoteAddress LocalSubnet (any protocol)
    Check ($r.Name -eq $RuleName -and $r.Direction -eq 1 -and $r.Action -eq 1 -and $r.Enabled -and $r.Profiles -eq 2 -and $r.Protocol -eq 256 -and $r.RemoteAddresses -eq 'LocalSubnet') "the rule: '$($r.Name)', inbound, allow, enabled, Private only, any protocol, remote $($r.RemoteAddresses)"
    $ns = @(Get-NetFirewallRule -DisplayName $RuleName)
    $nsApp = $ns[0] | Get-NetFirewallApplicationFilter
    $nsAddr = $ns[0] | Get-NetFirewallAddressFilter
    Check ($ns.Count -eq 1 -and "$($ns[0].Direction)" -eq 'Inbound' -and "$($ns[0].Action)" -eq 'Allow' -and "$($ns[0].Profile)" -eq 'Private' -and "$($ns[0].Enabled)" -eq 'True' -and $nsApp.Program -ieq $exe -and "$($nsAddr.RemoteAddress)" -eq 'LocalSubnet') "Get-NetFirewallRule: Inbound Allow Profile $($ns[0].Profile) RemoteAddress $($nsAddr.RemoteAddress) Program $($nsApp.Program) (the app's own rule has the same parameters)"
    if ($Full) {
        Check ($r.Description -ceq $strings['FirewallRuleDescription']) "rule description in $culture"
        $app = Invoke-AppFirewallCheck $exe
        Write-Host "    $($app.Output)"
        Check ($app.ExitCode -eq 0 -and $app.Output -match 'rules read True, private allow rule True, .*block rule False; startup prompt on a Private network: None') "the installed app's startup check finds the rule: no firewall prompt on first start (exit code $($app.ExitCode))"
    }
    Check ((Get-ShortcutMarker) -eq 1) 'shortcut component key path HKCU\Software\HomePodCast\Installer StartMenuShortcut is set'
}

function Assert-Uninstalled([string] $upgradeCode, [string[]] $folders) {
    $arp = @(Get-ArpEntries)
    Check ($arp.Count -eq 0) 'no HomePodCast in Programs and Features'
    Check (@(Get-RelatedProducts $upgradeCode).Count -eq 0) 'Windows Installer knows no product with the upgrade code'
    foreach ($folder in $folders) { Check (-not (Test-Path $folder)) "$folder is gone" }
    Check (@(Get-Shortcuts).Count -eq 0) 'no HomePodCast shortcut left'
    $rules = @(Get-FirewallRules | Where-Object { $_.Name -eq $RuleName -or ($_.ApplicationName -and $_.ApplicationName -like '*\HomePodCast.exe') })
    Check ($rules.Count -eq 0) "no firewall rule named '$RuleName' or for a HomePodCast.exe$(if ($rules) { ': ' + (($rules | ForEach-Object { "$($_.Name) $($_.ApplicationName)" }) -join '; ') })"
    Check ($null -eq (Get-ShortcutMarker)) 'HKCU\Software\HomePodCast\Installer StartMenuShortcut removed'
    $now = Get-AppDataSnapshot
    Check ($now -ceq $script:AppDataBefore) "settings in $AppDataDir untouched ($(@(Get-ChildItem $AppDataDir -File).Name -join ', '))"
}

function Get-ReleaseMsi([string] $tag) {
    if ($tag -eq 'latest') {
        $tag = @(gh release list -R $Repo --limit 20 --json tagName,isDraft --jq '.[] | select(.isDraft | not) | .tagName')[0]
        if ($LASTEXITCODE -or -not $tag) { throw "gh release list -R $Repo failed" }
        Write-Host "  latest release: $tag"
    }
    $dir = Join-Path $WorkDir "releases/$tag"
    New-Item -ItemType Directory -Force $dir | Out-Null
    gh release download $tag -R $Repo -p '*.msi' -D $dir --clobber
    if ($LASTEXITCODE) { throw "gh release download $tag -R $Repo failed" }
    $files = @(Get-ChildItem $dir -Filter *.msi)
    if ($files.Count -ne 1) { throw "release $tag has $($files.Count) .msi files" }
    [pscustomobject]@{ Tag = $tag; Path = $files[0].FullName }
}

# ---------------------------------------------------------------------------------------------------------------------

Write-Section 'Setup'
$runtimes = @(Get-ChildItem (Join-Path $env:ProgramFiles 'dotnet\shared\Microsoft.WindowsDesktop.App') -Directory -Filter '10.*' -ErrorAction SilentlyContinue)
Check ($runtimes.Count -gt 0) ".NET 10 Desktop Runtime for the MSI's launch condition: $(@($runtimes.Name) -join ', ')"
Check (@(Get-ArpEntries).Count -eq 0 -and @(Get-RelatedProducts $New.UpgradeCode).Count -eq 0 -and -not (Test-Path $DefaultFolder) -and -not (Test-Path $CustomFolder)) 'HomePodCast is not installed on this runner'
$fw = Get-Service mpssvc
Write-Host "  Windows Firewall service: $($fw.Status); profiles: $((Get-NetFirewallProfile | ForEach-Object { "$($_.Name) enabled=$($_.Enabled)" }) -join ', ')"
if (-not (Test-Path $AppDataDir)) { New-Item -ItemType Directory $AppDataDir | Out-Null }
'{ "DeviceName": "msi-e2e", "LatencyMs": 105, "AutoConnect": false, "Language": "zh-CN" }' | Set-Content (Join-Path $AppDataDir 'config.json') -Encoding utf8
'msi-e2e: this file must survive every uninstall' | Set-Content (Join-Path $AppDataDir 'homepodcast.log') -Encoding utf8
(Get-Item (Join-Path $AppDataDir 'config.json')).LastWriteTimeUtc = [datetime]::new(2026, 1, 2, 3, 4, 5, [DateTimeKind]::Utc)
$script:AppDataBefore = Get-AppDataSnapshot
Write-Host "  seeded $AppDataDir`n$($script:AppDataBefore -replace '(?m)^', '    ')"

Test-Languages $New

Write-Section '1. Silent install into the default folder'
Invoke-Msiexec 'install-default' "/i `"$Msi`"" | Out-Null
Assert-Installed $New $DefaultFolder 1033 -notIn $CustomFolder -Full

Write-Section '3a. Uninstall (default folder)'
Invoke-Msiexec 'uninstall-default' "/x $($New.ProductCode)" | Out-Null
Assert-Uninstalled $New.UpgradeCode $DefaultFolder

Write-Section '2. Silent install into a custom folder'
Invoke-Msiexec 'install-custom' "/i `"$Msi`" INSTALLFOLDER=`"$CustomFolder`"" | Out-Null
Assert-Installed $New $CustomFolder 1033 -notIn $DefaultFolder -Full

Write-Section '3b. Uninstall (custom folder)'
Invoke-Msiexec 'uninstall-custom' "/x $($New.ProductCode)" | Out-Null
Assert-Uninstalled $New.UpgradeCode $CustomFolder

$seen = @{}
$upgrades = 0
foreach ($release in $OldReleases) {
    Write-Section "4. Upgrade from $release"
    $old = Get-ReleaseMsi $release
    if ($seen.ContainsKey($old.Tag)) { Write-Host "  $($old.Tag) was tested above"; continue }
    $seen[$old.Tag] = $true
    $oldInfo = Get-MsiInfo $old.Path
    Write-Host "  $($old.Tag): $(Split-Path $old.Path -Leaf), MSI $($oldInfo.ProductVersion), languages $($oldInfo.Languages -join ',')"
    Check ($oldInfo.UpgradeCode -eq $New.UpgradeCode) "$($old.Tag) has the same upgrade code $($oldInfo.UpgradeCode)"
    Check ($oldInfo.ProductCode -ne $New.ProductCode) "$($old.Tag) is another product ($($oldInfo.ProductCode))"

    if ($oldInfo.Version3 -le $New.Version3) {
        # Alternate the languages: old English -> new Chinese, then old Chinese -> new English, ...
        $newLanguage = if ($upgrades % 2 -eq 0) { 2052 } else { 1033 }
        $oldLanguage = if ($newLanguage -eq 2052 -or 2052 -notin $oldInfo.Languages) { 1033 } else { 2052 }
        $upgrades++
        $oldTransform = if ($oldLanguage -ne 1033) { " TRANSFORMS=:$oldLanguage" } else { '' }
        $newTransform = if ($newLanguage -ne 1033) { " TRANSFORMS=:$newLanguage" } else { '' }
        Invoke-Msiexec "install-$($old.Tag)" "/i `"$($old.Path)`"$oldTransform" | Out-Null
        Assert-Installed $oldInfo $DefaultFolder $oldLanguage
        Invoke-Msiexec "upgrade-from-$($old.Tag)" "/i `"$Msi`"$newTransform" | Out-Null
        Check ($Installer.ProductState($oldInfo.ProductCode) -ne 5) "$($old.Tag) ($($oldInfo.ProductCode)) is no longer installed"
        Assert-Installed $New $DefaultFolder $newLanguage -notIn $CustomFolder -Full
        Invoke-Msiexec "uninstall-after-$($old.Tag)" "/x $($New.ProductCode)" | Out-Null
        Assert-Uninstalled $New.UpgradeCode $DefaultFolder
    }
    else {
        Write-Host "  $($old.Tag) (MSI $($oldInfo.ProductVersion)) is newer than this build (MSI $($New.ProductVersion)): the upgrade from it is tested on builds of a later version (tags); here the MSI must refuse the downgrade"
        Invoke-Msiexec "install-$($old.Tag)" "/i `"$($old.Path)`"" | Out-Null
        Assert-Installed $oldInfo $DefaultFolder 1033
        $result = Invoke-Msiexec "downgrade-over-$($old.Tag)" "/i `"$Msi`"" -expect 1603
        $message = (Get-WxlStrings 'en-US')['DowngradeError']
        Check ([bool](Select-String -Path $result.Log -SimpleMatch $message -Quiet)) "refused with its message: '$message'"
        Assert-Installed $oldInfo $DefaultFolder 1033
        Invoke-Msiexec "uninstall-$($old.Tag)" "/x $($oldInfo.ProductCode)" | Out-Null
        Assert-Uninstalled $New.UpgradeCode $DefaultFolder
    }
}
Check ($upgrades -gt 0) "$upgrades real upgrade(s) tested"

Write-Host ''
Write-Host "All $($script:Passed.Count) checks passed."
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @("## MSI end-to-end test: $($script:Passed.Count) checks passed", '', "$(Split-Path $Msi -Leaf) (MSI $($New.ProductVersion))", '')
    $summary += $script:Passed | ForEach-Object { "- $_" }
    $summary | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
}
