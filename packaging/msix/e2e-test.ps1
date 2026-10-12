<#
.SYNOPSIS
  End-to-end test of the Microsoft Store package on a throwaway Windows machine (CI: the msix-e2e job in
  .github/workflows/build.yml).

.DESCRIPTION
  Does what docs/device-test-checklist.zh-CN.md section 11 (Microsoft Store) asks a person to do, as far as it goes
  without a person, a speaker or a microphone:
    1. Install: signs a copy of the unsigned package with a throwaway self-signed certificate whose subject is the
       manifest's Publisher (trusted in LocalMachine\TrustedPeople) and installs that copy with Add-AppxPackage. The
       package given stays unsigned, as the Store wants it (the Store signs what it publishes).
    2. Firewall: the manifest's two rules for the package's exe exist (inbound, Private, enabled, allow, TCP and UDP).
    3. Start: seeds the package's settings (no speaker, automatic connect off), starts the app the way Start does
       (explorer.exe shell:AppsFolder\<family name>!HomePodCast) and checks that it runs with package identity, that its
       log is in the package's storage and says it runs from the package, that it never contacts GitHub, and that it
       found the package's firewall rules and took no firewall prompt or UAC path.
    4. Single instance: the package started again (Start, and the AppX activation API), and the MSI build installed
       silently and started, leave the one HomePodCast process that was running.
    5. Startup task: `HomePodCast.exe autostart on|off|status` activated inside the package (the same code as the app's
       autostart switch) turns the task on and off; checked in the app's log and in the registry where Windows keeps
       the task's state.
    6. Uninstall: the package, its data folder (settings and log), its firewall rules and its startup task are gone.

  Nothing connects anywhere: the settings have no speaker and automatic connect off, and the Store version never
  contacts GitHub. The app's log is copied to -OutDir before the uninstall.

  It installs a certificate, the package and the MSI, and changes the sideloading policy if needed: it refuses to run
  outside GitHub Actions unless -Force is given (a throwaway test VM, never an everyday PC). Windows PowerShell 5.1
  (the Appx module), as administrator. ASCII only: Windows PowerShell reads a script without a BOM as ANSI.

.EXAMPLE
  ./packaging/msix/e2e-test.ps1 -Msix msix\HomePodCast-1.0.0-x64.msix -Msi packages\HomePodCast-1.0.0-x64.msi
#>
param(
    # The unsigned package (the msix artifact of build.yml). Not changed: a signed copy is installed.
    [Parameter(Mandatory)] [string] $Msix,
    # The MSI (the packages artifact), for the single-instance check against an unpackaged copy. Optional.
    [string] $Msi = '',
    # Where the signed copy, the app's log and the MSI log go.
    [string] $OutDir = 'msix-e2e',
    # Run outside GitHub Actions (on a throwaway test VM).
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.Encoding]::UTF8

if (-not $env:GITHUB_ACTIONS -and -not $Force) {
    throw 'This test installs a certificate, the package and the MSI on this PC and changes them. Run it in CI, or with -Force on a throwaway test VM.'
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run it as administrator (the certificate goes into LocalMachine\TrustedPeople).' }

# ------------------------------------------------------------------------------------------------ reporting

$script:Passed = New-Object Collections.Generic.List[string]
$script:Failed = New-Object Collections.Generic.List[string]

function Step([string] $title) {
    Write-Host ''
    Write-Host "== $title" -ForegroundColor Cyan
}

function Check([bool] $ok, [string] $what, [string] $detail = '') {
    if ($ok) {
        Write-Host "PASS  $what" -ForegroundColor Green
        $script:Passed.Add($what)
    }
    else {
        $text = if ($detail) { "$what ($detail)" } else { $what }
        Write-Host "FAIL  $text" -ForegroundColor Red
        if ($env:GITHUB_ACTIONS) { Write-Host "::error title=msix-e2e::$text" }
        $script:Failed.Add($text)
    }
}

function Info([string] $text) { Write-Host "      $text" }

# Polls $probe (a script block) until it returns something truthy or $seconds pass; returns the last result.
function Wait-For([scriptblock] $probe, [int] $seconds = 30, [int] $intervalMs = 500) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($true) {
        $result = & $probe
        if ($result -or $clock.Elapsed.TotalSeconds -ge $seconds) { return $result }
        Start-Sleep -Milliseconds $intervalMs
    }
}

# ------------------------------------------------------------------------------------------------ native helpers

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace MsixE2e
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            int options, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    internal class ApplicationActivationManager { }

    public sealed class Run
    {
        public uint ProcessId;
        public int? ExitCode; // null: it had ended before it could be opened
    }

    public static class Native
    {
        private const int AoNoErrorUi = 2;
        private const uint Synchronize = 0x00100000, QueryLimitedInformation = 0x1000;
        private const int AppModelErrorNoPackage = 15700, ErrorInsufficientBuffer = 122;

        // IApplicationActivationManager.ActivateApplication: how Windows starts a packaged app (Start, a tile); the
        // arguments reach a full-trust app's command line. Returns the new process's id.
        public static uint Activate(string appUserModelId, string arguments)
        {
            var manager = (IApplicationActivationManager)new ApplicationActivationManager();
            try
            {
                uint processId;
                int hr = manager.ActivateApplication(appUserModelId, arguments, AoNoErrorUi, out processId);
                if (hr < 0) Marshal.ThrowExceptionForHR(hr);
                return processId;
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }

        // Activates and waits for the process to end.
        public static Run ActivateAndWait(string appUserModelId, string arguments, int timeoutMs)
        {
            var run = new Run { ProcessId = Activate(appUserModelId, arguments) };
            IntPtr process = OpenProcess(Synchronize | QueryLimitedInformation, false, run.ProcessId);
            if (process == IntPtr.Zero) return run;
            try
            {
                if (WaitForSingleObject(process, (uint)timeoutMs) != 0)
                    throw new TimeoutException("process " + run.ProcessId + " (" + arguments + ") still runs after " + timeoutMs + " ms");
                uint code;
                if (!GetExitCodeProcess(process, out code)) throw new Win32Exception();
                run.ExitCode = (int)code;
                return run;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // The process's package full name; null when it has no package identity.
        public static string PackageFullName(int processId)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, (uint)processId);
            if (process == IntPtr.Zero) throw new Win32Exception();
            try
            {
                uint length = 0;
                int rc = GetPackageFullName(process, ref length, null);
                if (rc == AppModelErrorNoPackage) return null;
                if (rc != ErrorInsufficientBuffer) throw new Win32Exception(rc);
                var name = new StringBuilder((int)length);
                rc = GetPackageFullName(process, ref length, name);
                if (rc != 0) throw new Win32Exception(rc);
                return name.ToString();
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // The process's exe; null when it can't be opened (it ended).
        public static string ImagePath(int processId)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, (uint)processId);
            if (process == IntPtr.Zero) return null;
            try
            {
                var name = new StringBuilder(1024);
                int size = name.Capacity;
                return QueryFullProcessImageName(process, 0, name, ref size) ? name.ToString() : null;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        // The process's top-level WinForms window with this title, visible or not; IntPtr.Zero if none.
        public static IntPtr FindWindow(int processId, string title)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows((window, unused) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner != (uint)processId) return true;
                var text = new StringBuilder(256);
                GetWindowText(window, text, text.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(window, cls, cls.Capacity);
                if (text.ToString() != title || !cls.ToString().StartsWith("WindowsForms")) return true;
                found = window;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        public static bool IsVisible(IntPtr window) { return IsWindowVisible(window); }

        // What the window's close button sends (WM_SYSCOMMAND SC_CLOSE): WinForms sees CloseReason.UserClosing.
        public static void CloseToTray(IntPtr window)
        {
            if (!PostMessage(window, 0x0112, new IntPtr(0xF060), IntPtr.Zero)) throw new Win32Exception();
        }

        // The package's SID (S-1-15-2-...), as firewall rules name a package.
        public static string PackageSid(string familyName)
        {
            IntPtr sid;
            int hr = DeriveAppContainerSidFromAppContainerName(familyName, out sid);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try { return new SecurityIdentifier(sid).Value; }
            finally { FreeSid(sid); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetPackageFullName(IntPtr process, ref uint length, StringBuilder name);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder name, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
        private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

        [DllImport("advapi32.dll")]
        private static extern IntPtr FreeSid(IntPtr sid);
    }
}
'@

# ------------------------------------------------------------------------------------------------ other helpers

function Find-SdkTool([string] $name) {
    # Windows Kits\10\bin\<version>\x64\<tool>, newest SDK first (as build.ps1 finds makeappx).
    $found = @(foreach ($root in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if (-not $root) { continue }
        $kit = Join-Path $root 'Windows Kits\10\bin'
        if (-not (Test-Path $kit)) { continue }
        Get-ChildItem $kit -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | ForEach-Object {
            $tool = Join-Path $_.FullName "x64\$name"
            if (Test-Path $tool) { [pscustomobject]@{ Version = [version]$_.Name; Path = $tool } }
        }
    })
    if ($found.Count) { return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path }
    throw "$name not found (Windows SDK)"
}

# The log's lines; the app keeps it open for writing, so it is read with ReadWrite sharing.
function Read-Log([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try {
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        return @($reader.ReadToEnd() -split "`r?`n" | Where-Object { $_ })
    }
    finally {
        $stream.Dispose()
    }
}

function Find-Line([string[]] $lines, [string] $pattern) { @($lines | Where-Object { $_ -match $pattern }) }

function Get-HomePodCastProcesses { @(Get-Process -Name HomePodCast -ErrorAction SilentlyContinue) }

# The firewall rules for an exe (by path, environment variables expanded) or a package (by SID), from every store.
function Get-AppFirewallRules([string] $exe, [string] $packageSid) {
    $filters = @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore | Where-Object {
        ($_.Program -and $_.Program -ne 'Any' -and [Environment]::ExpandEnvironmentVariables($_.Program) -ieq $exe) -or
        ($packageSid -and $_.Package -eq $packageSid)
    })
    foreach ($filter in $filters) {
        $rule = $filter | Get-NetFirewallRule
        $ports = $rule | Get-NetFirewallPortFilter
        [pscustomobject]@{
            DisplayName = $rule.DisplayName
            Group       = $rule.Group
            Direction   = "$($rule.Direction)"
            Action      = "$($rule.Action)"
            Enabled     = "$($rule.Enabled)"
            Profile     = "$($rule.Profile)"
            Protocol    = "$($ports.Protocol)"
            Program     = $filter.Program
            Package     = $filter.Package
        }
    }
}

# Where Windows keeps a packaged app's startup task state (what Settings > Apps > Startup and Task Manager show).
function Get-StartupTaskKey([string] $familyName) {
    "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$familyName"
}

function Get-StartupTaskState([string] $familyName, [string] $taskId) {
    $key = Join-Path (Get-StartupTaskKey $familyName) $taskId
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $state = (Get-ItemProperty -LiteralPath $key).PSObject.Properties['State']
    if ($state) { return [int]$state.Value }
    return $null
}

function Show-RegistryTree([string] $key) {
    if (-not (Test-Path -LiteralPath $key)) { Info "(no $key)"; return }
    foreach ($item in @(Get-Item -LiteralPath $key) + @(Get-ChildItem -LiteralPath $key -Recurse)) {
        $values = ($item.Property | ForEach-Object { "$_=$($item.GetValue($_))" }) -join ', '
        Info "$($item.Name): $values"
    }
}

# ------------------------------------------------------------------------------------------------ the package

$Msix = (Resolve-Path $Msix).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$cleanup = New-Object Collections.Generic.List[scriptblock]
$guiPid = $null

try {
    Step "Package $Msix"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Msix)
    try {
        $entries = @($zip.Entries | ForEach-Object { $_.FullName })
        $reader = New-Object IO.StreamReader($zip.GetEntry('AppxManifest.xml').Open())
        [xml] $manifest = $reader.ReadToEnd()
        $reader.Dispose()
    }
    finally {
        $zip.Dispose()
    }
    $identityName = $manifest.Package.Identity.GetAttribute('Name')
    $publisher = $manifest.Package.Identity.GetAttribute('Publisher')
    Info "identity $identityName, publisher $publisher, version $($manifest.Package.Identity.GetAttribute('Version')), $($entries.Count) files"
    Check (-not ($entries -contains 'AppxSignature.p7x')) 'the msix artifact is unsigned (the Store signs it)'

    # ---------------------------------------------------------------------------------------- 1. install
    Step '1. Install: a signed copy, trusted through a throwaway certificate'
    $unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    $allow = (Get-ItemProperty -LiteralPath $unlock -ErrorAction SilentlyContinue)
    $allowTrusted = if ($allow -and $allow.PSObject.Properties['AllowAllTrustedApps']) { $allow.AllowAllTrustedApps } else { $null }
    Info "sideloading policy: AllowAllTrustedApps=$allowTrusted"
    if ($allowTrusted -ne 1) {
        if (-not (Test-Path -LiteralPath $unlock)) { New-Item -Path $unlock -Force | Out-Null }
        Set-ItemProperty -LiteralPath $unlock -Name AllowAllTrustedApps -Value 1 -Type DWord
        Info 'AllowAllTrustedApps set to 1 for the test'
        $cleanup.Add({
            if ($null -eq $allowTrusted) { Remove-ItemProperty -LiteralPath $unlock -Name AllowAllTrustedApps -ErrorAction SilentlyContinue }
            else { Set-ItemProperty -LiteralPath $unlock -Name AllowAllTrustedApps -Value $allowTrusted -Type DWord }
        }.GetNewClosure())
    }

    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'HomePodCast msix-e2e (throwaway)' -CertStoreLocation Cert:\CurrentUser\My `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddDays(1)
    $thumbprint = $cert.Thumbprint
    $cleanup.Add({ Remove-Item "Cert:\CurrentUser\My\$thumbprint" -ErrorAction SilentlyContinue }.GetNewClosure())
    $cer = Join-Path $OutDir 'msix-e2e.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    $cleanup.Add({ Remove-Item "Cert:\LocalMachine\TrustedPeople\$thumbprint" -ErrorAction SilentlyContinue }.GetNewClosure())
    Info "certificate $thumbprint, subject $($cert.Subject), trusted in LocalMachine\TrustedPeople"

    $signed = Join-Path $OutDir ('signed-' + [IO.Path]::GetFileName($Msix))
    Copy-Item $Msix $signed -Force
    $signtool = Find-SdkTool 'signtool.exe'
    & $signtool sign /fd SHA256 /sha1 $thumbprint /s My $signed
    if ($LASTEXITCODE) { throw "signtool sign failed ($LASTEXITCODE)" }
    $cleanup.Add({ Remove-Item $signed -ErrorAction SilentlyContinue }.GetNewClosure())

    $existing = @(Get-AppxPackage -Name $identityName)
    if ($existing.Count) { throw "$identityName is installed already ($($existing[0].PackageFullName)): this test needs a machine without it" }
    Add-AppxPackage -Path $signed
    $pkg = Get-AppxPackage -Name $identityName
    Check ($null -ne $pkg) "Add-AppxPackage installed $identityName"
    if (-not $pkg) { throw 'the package is not installed' }
    $cleanup.Add({
        $left = Get-AppxPackage -Name $identityName
        if ($left) { Remove-AppxPackage -Package $left.PackageFullName -ErrorAction SilentlyContinue }
    }.GetNewClosure())
    # Kept as plain values: the package object reads some of them from Windows, and they are gone after the uninstall.
    $family = $pkg.PackageFamilyName
    $packageFullName = $pkg.PackageFullName
    $installFolder = $pkg.InstallLocation
    $aumid = "$family!HomePodCast"
    $exe = Join-Path $installFolder 'HomePodCast.exe'
    $data = Join-Path $env:LOCALAPPDATA "Packages\$family"
    $packageSid = [MsixE2e.Native]::PackageSid($family)
    Info "full name $packageFullName, status $($pkg.Status), signature $($pkg.SignatureKind)"
    Info "installed in $installFolder"
    Info "package SID $packageSid"
    if ($identityName -eq 'qiyinxi.HomePodCast' -and $publisher -eq 'CN=874AAE1B-1F37-40C7-80D9-3146C953705B') {
        Check ($family -eq 'qiyinxi.HomePodCast_21rkx5qfj0b6t') "package family name $family (as the docs name it)"
    }
    Check (Test-Path -LiteralPath $exe) 'HomePodCast.exe is in the install folder'

    # ---------------------------------------------------------------------------------------- 2. firewall
    Step '2. Firewall: the manifest''s rules'
    $rules = @(Wait-For { @(Get-AppFirewallRules $exe $packageSid) } 30)
    $rules | Format-Table DisplayName, Direction, Action, Enabled, Profile, Protocol, Group -AutoSize | Out-String -Width 220 | Write-Host
    foreach ($rule in $rules) { Info "program $($rule.Program), package $($rule.Package)" }
    Check ($rules.Count -eq 2) 'two firewall rules for the package''s exe' "found $($rules.Count)"
    Check (@($rules | Where-Object { $_.Direction -eq 'Inbound' -and $_.Action -eq 'Allow' -and $_.Enabled -eq 'True' }).Count -eq $rules.Count -and $rules.Count) `
        'every rule is inbound, allow, enabled'
    Check (@($rules | Where-Object { $_.Profile -eq 'Private' }).Count -eq $rules.Count -and $rules.Count) 'every rule is for Private networks only'
    $protocols = (@($rules | ForEach-Object { $_.Protocol }) | Sort-Object) -join ','
    Check ($protocols -eq 'TCP,UDP') 'one rule for TCP, one for UDP' "protocols: $protocols"

    # ---------------------------------------------------------------------------------------- 3. start
    Step '3. Start it the way Start does'
    $realFolder = Join-Path $env:APPDATA 'HomePodCast'
    if (Test-Path -LiteralPath $realFolder) { throw "$realFolder exists: this test needs a machine where no HomePodCast ran before" }
    Info "package data folder $data exists after the install: $(Test-Path -LiteralPath $data)"
    # The package's %APPDATA% writes go to its own storage, and it reads files there as if they were in %APPDATA%:
    # the settings seeded there are the ones it starts with. No speaker, no automatic connect; a dark theme shows in
    # the log that this file was read.
    $settings = Join-Path $data 'LocalCache\Roaming\HomePodCast'
    New-Item -ItemType Directory -Force $settings | Out-Null
    $config = '{ "DeviceId": null, "AutoConnect": false, "AutoUpdateCheck": false, "Theme": "dark" }'
    [IO.File]::WriteAllText((Join-Path $settings 'config.json'), $config)
    Info "seeded $settings\config.json: $config"
    $log = Join-Path $settings 'homepodcast.log'

    # explorer.exe shell:AppsFolder\<AUMID> is what Start does, and what the app itself runs to restart after a change
    # of the UI language (AppEnvironment.Relaunch).
    Start-Process (Join-Path $env:WINDIR 'explorer.exe') "shell:AppsFolder\$aumid"
    Info "explorer.exe shell:AppsFolder\$aumid"
    $gui = Wait-For { Get-HomePodCastProcesses | Select-Object -First 1 } 60
    Check ($null -ne $gui) 'explorer.exe shell:AppsFolder started the app'
    if (-not $gui) {
        # Go on with the AppX activation API, to see what else works.
        $id = [MsixE2e.Native]::Activate($aumid, '')
        Info "activated through the AppX activation API instead: process $id"
        $gui = Wait-For { Get-HomePodCastProcesses | Select-Object -First 1 } 60
        if (-not $gui) { throw 'no HomePodCast process' }
    }
    $guiPid = $gui.Id
    $image = [MsixE2e.Native]::ImagePath($guiPid)
    Info "process $guiPid, $image"
    Check ($image -ieq $exe) 'the process runs the package''s exe' $image
    $fullName = [MsixE2e.Native]::PackageFullName($guiPid)
    Check ($fullName -eq $packageFullName) 'the process has package identity (GetPackageFullName)' "GetPackageFullName: $fullName"

    $patterns = [ordered]@{
        start   = ' starting from the package ' + [regex]::Escape($family) + ' \(Microsoft Store version\)$'
        update  = 'update: Microsoft Store package: never contacts GitHub'
        theme   = 'UI language \S+, theme dark '
        network = '(INFO|WARN)  network: '
    }
    $lines = @(Wait-For {
        $now = @(Read-Log $log)
        if (@($patterns.Values | Where-Object { -not (Find-Line $now $_) }).Count -eq 0) { $now }
    } 60)
    if (-not $lines.Count) { $lines = @(Read-Log $log) }
    Info "log $log ($($lines.Count) lines)"
    $startLines = @(Find-Line $lines 'HomePodCast \S+ starting')
    Check (Test-Path -LiteralPath $log) 'the log is in the package''s storage (LocalCache\Roaming\HomePodCast)'
    Check ($startLines.Count -eq 1 -and $startLines[0] -match $patterns.start) `
        "the startup line ends with 'starting from the package $family (Microsoft Store version)'" ($startLines -join ' | ')
    Check ([bool](Find-Line $lines $patterns.update)) "the log has 'update: Microsoft Store package: never contacts GitHub'"
    Check (-not (Find-Line $lines 'update: (automatic check due|checking|found)')) 'no update check was scheduled or made'
    Check ([bool](Find-Line $lines $patterns.theme)) 'the seeded settings were read (theme dark)'
    Check ([bool](Find-Line $lines $patterns.network)) 'the firewall and network check ran at startup' 'no network: line'
    Check (-not (Find-Line $lines 'no rule of the package found')) 'the app found the package''s firewall rules (no "no rule of the package found")'
    Check (-not (Find-Line $lines 'firewall rule added|never adds a rule|firewall check failed')) 'no firewall prompt or UAC path was taken'
    Check (-not (Find-Line $lines ' (ERROR|FATAL) |fatal: ')) 'no errors in the log' (@(Find-Line $lines ' (ERROR|FATAL) |fatal: ') -join ' | ')
    Check (-not (Test-Path -LiteralPath $realFolder)) 'nothing was written to the real %APPDATA%\HomePodCast'
    $streamLines = @(Find-Line $lines ' stream: |connected to |stream ready')
    Check ($streamLines.Count -eq 0) 'no connection to a speaker was tried (none in the settings)' ($streamLines -join ' | ')
    $tcp = @(Get-NetTCPConnection -OwningProcess $guiPid -ErrorAction SilentlyContinue)
    foreach ($c in $tcp) { Info "tcp $($c.State) $($c.LocalAddress):$($c.LocalPort) -> $($c.RemoteAddress):$($c.RemotePort)" }
    $outside = @($tcp | Where-Object { $_.State -ne 'Listen' -and $_.RemoteAddress -notin '127.0.0.1', '::1', '0.0.0.0', '::' })
    Check ($outside.Count -eq 0) 'no TCP connection leaves the machine' (@($outside | ForEach-Object { "$($_.RemoteAddress):$($_.RemotePort)" }) -join ', ')

    # ---------------------------------------------------------------------------------------- 4. single instance
    Step '4. Single instance'
    # The main window (Start passes no --tray, so it is open), closed to the tray before a second start the way the
    # user closes it (its close button: SC_CLOSE only hides it). A second start must bring it back.
    $main = [MsixE2e.Native]::FindWindow($guiPid, 'HomePodCast')
    Info ("main window 0x{0:X}, visible: {1}" -f $main.ToInt64(), ($main -ne [IntPtr]::Zero -and [MsixE2e.Native]::IsVisible($main)))
    Check ($main -ne [IntPtr]::Zero) 'the app has its main window'
    function Close-ToTray([string] $before) {
        if ($main -eq [IntPtr]::Zero) { return }
        [MsixE2e.Native]::CloseToTray($main)
        $hidden = Wait-For { -not [MsixE2e.Native]::IsVisible($main) } 10
        Check $hidden "the window closed to the tray (before $before)"
    }
    function Check-Shown([string] $by) {
        if ($main -eq [IntPtr]::Zero) { return }
        $shown = Wait-For { [MsixE2e.Native]::IsVisible($main) } 15
        Check $shown "$by brought the running copy's window back"
    }

    Close-ToTray 'the second start from Start'
    Start-Process (Join-Path $env:WINDIR 'explorer.exe') "shell:AppsFolder\$aumid"
    Check-Shown 'starting it again from Start'
    Start-Sleep -Seconds 5
    $ids = @(Get-HomePodCastProcesses | ForEach-Object { $_.Id })
    Check ($ids.Count -eq 1 -and $ids[0] -eq $guiPid) 'started again from Start: still the one process' "processes: $($ids -join ', ')"

    Close-ToTray 'the activation'
    $run = [MsixE2e.Native]::ActivateAndWait($aumid, '', 30000)
    Info "activated again (AppX activation API): process $($run.ProcessId), exit code $($run.ExitCode)"
    Check-Shown 'activating it again'
    $ids = @(Get-HomePodCastProcesses | ForEach-Object { $_.Id })
    Check ($ids.Count -eq 1 -and $ids[0] -eq $guiPid -and $run.ProcessId -ne $guiPid) `
        'activated again: the new process ended and left the one that was running' "processes: $($ids -join ', ')"
    Check ($null -eq $run.ExitCode -or $run.ExitCode -eq 0) 'the second copy exited with 0' "exit code $($run.ExitCode)"
    Check (@(Find-Line (Read-Log $log) 'HomePodCast \S+ starting').Count -eq 1) 'the second copies never opened the log'

    if ($Msi) {
        $Msi = (Resolve-Path $Msi).Path
        $msiLog = Join-Path $OutDir 'msi-install.log'
        $install = Start-Process msiexec.exe -ArgumentList "/i `"$Msi`" /qn /norestart /l*v `"$msiLog`"" -Wait -PassThru
        Check ($install.ExitCode -in 0, 3010) 'the MSI installed silently' "msiexec exit code $($install.ExitCode)"
        $cleanup.Add({ Start-Process msiexec.exe -ArgumentList "/x `"$Msi`" /qn /norestart" -Wait }.GetNewClosure())
        $msiExe = Join-Path $env:ProgramFiles 'HomePodCast\HomePodCast.exe'
        if (Test-Path -LiteralPath $msiExe) {
            # Should it get past the lock after all: no speaker, no automatic connect there either.
            New-Item -ItemType Directory -Force $realFolder | Out-Null
            [IO.File]::WriteAllText((Join-Path $realFolder 'config.json'), $config)
            Close-ToTray 'the MSI build starts'
            $msiRun = Start-Process $msiExe -PassThru
            $null = $msiRun.Handle # kept, so that ExitCode is there after the exit
            $ended = $msiRun.WaitForExit(30000)
            Info "MSI build $msiExe, process $($msiRun.Id), ended: $ended"
            if (-not $ended) { $msiRun.Kill() }
            Check-Shown 'the MSI build'
            $ids = @(Get-HomePodCastProcesses | ForEach-Object { $_.Id })
            Check ($ended -and $msiRun.ExitCode -eq 0) 'the MSI build started next to it exited with 0' "ended $ended, exit code $(if ($ended) { $msiRun.ExitCode })"
            Check ($ids.Count -eq 1 -and $ids[0] -eq $guiPid) 'the MSI build left the packaged one running alone' "processes: $($ids -join ', ')"
            Check (-not (Test-Path -LiteralPath (Join-Path $realFolder 'homepodcast.log'))) 'the MSI build never opened its log (it stopped at the lock)'
        }
        else {
            Check $false 'the MSI installed HomePodCast.exe' "no $msiExe"
        }
    }

    Step 'The packaged app stops'
    Stop-Process -Id $guiPid -Force
    Wait-For { -not (Get-Process -Id $guiPid -ErrorAction SilentlyContinue) } 15 | Out-Null
    $guiPid = $null

    # ---------------------------------------------------------------------------------------- 5. startup task
    Step '5. Startup task, through the app''s own code (HomePodCast.exe autostart, activated in the package)'
    $taskId = 'HomePodCastStartup'
    function Invoke-Autostart([string] $what, [string] $expectLine, $expectState) {
        $before = (Read-Log $log).Count
        $run = [MsixE2e.Native]::ActivateAndWait($aumid, "autostart $what", 60000)
        $new = @(Read-Log $log | Select-Object -Skip $before)
        $state = Get-StartupTaskState $family $taskId
        Info "autostart $what -> process $($run.ProcessId), exit code $($run.ExitCode), registry State=$state"
        foreach ($line in $new) { Info "log: $line" }
        Check ($null -eq $run.ExitCode -or $run.ExitCode -eq 0) "autostart $what exited with 0" "exit code $($run.ExitCode)"
        Check ([bool](Find-Line $new ([regex]::Escape($expectLine) + '$'))) "the log has '$expectLine'" ($new -join ' | ')
        if ($null -ne $expectState) { Check ($state -eq $expectState) "the startup task's state in the registry is $expectState" "State=$state" }
    }
    Invoke-Autostart 'status' 'autostart: startup task Disabled' $null
    Invoke-Autostart 'on' 'autostart: startup task enable requested, now Enabled' 2
    Invoke-Autostart 'status' 'autostart: startup task Enabled' 2
    Show-RegistryTree (Get-StartupTaskKey $family)
    Invoke-Autostart 'off' 'autostart: startup task disabled, now Disabled' 0
    # On again, so that the uninstall has a startup task to remove.
    Invoke-Autostart 'on' 'autostart: startup task enable requested, now Enabled' 2
    Check (@(Get-HomePodCastProcesses).Count -eq 0) 'the autostart commands left no process behind'

    $lines = Read-Log $log
    Copy-Item -LiteralPath $log (Join-Path $OutDir 'homepodcast.log') -Force

    # ---------------------------------------------------------------------------------------- 6. uninstall
    Step '6. Uninstall'
    Remove-AppxPackage -Package $packageFullName
    Check (-not (Get-AppxPackage -Name $identityName)) 'Remove-AppxPackage removed the package'
    $gone = Wait-For { -not (Test-Path -LiteralPath $data) } 60
    Check $gone "the package's data folder is gone ($data)"
    Wait-For { @(Get-AppFirewallRules $exe $packageSid).Count -eq 0 } 30 | Out-Null
    $leftRules = @(Get-AppFirewallRules $exe $packageSid)
    Check ($leftRules.Count -eq 0) 'the package''s firewall rules are gone' "$($leftRules.Count) left"
    $taskGone = Wait-For { -not (Test-Path -LiteralPath (Get-StartupTaskKey $family)) } 30
    if (-not $taskGone) { Show-RegistryTree (Get-StartupTaskKey $family) }
    Check $taskGone 'the startup task is gone (no SystemAppData entry for the package)'
    $runValues = @(foreach ($key in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run') {
        $props = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($props) { $props.PSObject.Properties | Where-Object { $_.Name -like '*HomePodCast*' -or "$($_.Value)" -like '*HomePodCast*' } | ForEach-Object { "$key\$($_.Name)" } }
    })
    $startupFolders = @([Environment]::GetFolderPath('Startup'), [Environment]::GetFolderPath('CommonStartup')) |
        Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { Get-ChildItem $_ -Filter '*HomePodCast*' }
    $startupCommands = @(Get-CimInstance Win32_StartupCommand | Where-Object { "$($_.Caption) $($_.Command)" -like '*HomePodCast*' })
    Check ($runValues.Count -eq 0 -and @($startupFolders).Count -eq 0 -and $startupCommands.Count -eq 0) 'no HomePodCast startup entry anywhere (Run keys, Startup folders)' `
        "$($runValues -join ', ') $(@($startupFolders) -join ', ') $(@($startupCommands | ForEach-Object Command) -join ', ')"
    $installGone = Wait-For { -not (Test-Path -LiteralPath $installFolder) } 30
    Info "install folder $installFolder removed: $installGone"

    Step "The app's log ($($lines.Count) lines; artifact msix-e2e-logs)"
    foreach ($line in $lines) { Info $line }
}
finally {
    Get-HomePodCastProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($script:Failed.Count -or -not $script:Passed.Count) {
        # A failed run: keep what the app wrote.
        if (Test-Path variable:log) {
            if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log (Join-Path $OutDir 'homepodcast.log') -Force -ErrorAction SilentlyContinue }
        }
    }
    for ($i = $cleanup.Count - 1; $i -ge 0; $i--) {
        try { & $cleanup[$i] } catch { Write-Warning "cleanup: $($_.Exception.Message)" }
    }
}

Step "Summary: $($script:Passed.Count) passed, $($script:Failed.Count) failed"
if ($env:GITHUB_STEP_SUMMARY) {
    $summary = @('### msix-e2e', '') + @($script:Passed | ForEach-Object { "- [x] $_" }) + @($script:Failed | ForEach-Object { "- [ ] **$_**" })
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary -Encoding UTF8
}
if ($script:Failed.Count) {
    $script:Failed | ForEach-Object { Write-Host "FAIL  $_" -ForegroundColor Red }
    exit 1
}
