<#
.SYNOPSIS
  Builds the HomePodCast MSIX package for the Microsoft Store (makeappx and makepri from the Windows SDK).

.DESCRIPTION
  Writes <OutDir>\HomePodCast-<Version>-x64.msix, unsigned (the Store signs what it publishes):
    1. publishes the app self-contained for win-x64 into packaging\msix\obj\layout. The Store doesn't install the
       .NET Desktop Runtime, so the package carries it (the MSI and the zip stay framework-dependent). The build
       goes to packaging\msix\obj\bin, so app\ is left alone; its intermediates go to src\obj's win-x64 subfolder;
    2. adds the logos (Assets\, made by tools\make_icon.py --msix) and LICENSE, and writes AppxManifest.xml from
       the template next to this script with the identity, display names and version filled in;
    3. makepri indexes the logos (scale- and targetsize-qualified files) into resources.pri (priconfig.xml), so
       the manifest can name each logo once and Windows picks the right size;
    4. makeappx pack validates the manifest against the schema, checks every file it names and packs the layout.
       Any error stops the build.

  Package version: X.Y.Z or X.Y.Z-<anything> -> X.Y.Z.0. The Store reserves the fourth field (it must be 0) and
  refuses a first field of 0, so a 0.x package is only good for testing; see README.md.

  Identity: the defaults are the values Partner Center assigned to the reserved name HomePodCast (Product
  management > Product identity); the Store refuses a package whose identity doesn't match them exactly. They
  aren't secrets. CI passes the repository variables MSIX_* instead when they are set.

.EXAMPLE
  ./packaging/msix/build.ps1 -Version 1.0.0-local.1
#>
param(
    # The CI version (build.yml "Version" step), e.g. 1.2.3, 1.2.3-beta.1, 1.2.3-dev.57.
    [Parameter(Mandatory)] [string] $Version,
    # Package/Identity/@Name ("Package/Identity/Name" in Partner Center, Product identity).
    [string] $IdentityName = 'qiyinxi.HomePodCast',
    # Package/Identity/@Publisher ("Package/Identity/Publisher").
    [string] $Publisher = 'CN=874AAE1B-1F37-40C7-80D9-3146C953705B',
    # Package/Properties/PublisherDisplayName ("Package/Properties/PublisherDisplayName").
    [string] $PublisherDisplayName = 'qiyinxi',
    # The reserved app name; the Store checks that Properties/DisplayName is one of the names reserved for the app.
    [string] $DisplayName = 'HomePodCast',
    [string] $OutDir = 'publish',
    # Folder holding makeappx.exe and makepri.exe. Default: the x64 tools of the newest installed Windows 10/11 SDK.
    [string] $SdkBinDir = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function ConvertTo-MsixVersion([string] $v) {
    if ($v -notmatch '^(\d+)\.(\d+)\.(\d+)(-.+)?$') { throw "version '$v' is not X.Y.Z or X.Y.Z-suffix" }
    $fields = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
    if (@($fields | Where-Object { $_ -gt 65535 }).Count) { throw "version '$v' is out of the package version range (65535.65535.65535)" }
    "$($fields -join '.').0"
}

function Find-SdkTool([string] $name) {
    if ($SdkBinDir) {
        $tool = Join-Path $SdkBinDir $name
        if (-not (Test-Path $tool)) { throw "$tool not found" }
        return $tool
    }
    # Windows Kits\10\bin\<version>\x64\<tool>, newest SDK first; the 64-bit tools also run on Arm64 (emulated).
    $kits = @(${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ } |
        ForEach-Object { Join-Path $_ 'Windows Kits\10\bin' } | Where-Object { Test-Path $_ }
    $found = @(foreach ($kit in $kits) {
        Get-ChildItem $kit -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | ForEach-Object {
            $tool = Join-Path $_.FullName "x64\$name"
            if (Test-Path $tool) { [pscustomobject]@{ Version = [version]$_.Name; Path = $tool } }
        }
    })
    if ($found.Count) { return ($found | Sort-Object Version -Descending | Select-Object -First 1).Path }
    $onPath = Get-Command $name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    throw "$name not found: install the Windows 10/11 SDK (it comes with Visual Studio's desktop C++ workload) or pass -SdkBinDir"
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$OutDir = [System.IO.Path]::GetFullPath($OutDir, $repo)
$msixVersion = ConvertTo-MsixVersion $Version
if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') { throw "identity name '$IdentityName' must be 3-50 letters, digits, dots or hyphens" }
if ($Publisher -notmatch '^CN=') { throw "publisher '$Publisher' must be a distinguished name such as CN=<GUID> (Partner Center, Product identity)" }
if ($msixVersion.StartsWith('0.')) {
    Write-Warning "package version $msixVersion starts with 0, which the Store refuses: this package is for testing only"
}

$makeappx = Find-SdkTool 'makeappx.exe'
$makepri = Find-SdkTool 'makepri.exe'
$work = Join-Path $PSScriptRoot 'obj'
$layout = Join-Path $work 'layout'
$priRoot = Join-Path $work 'pri'
$msix = Join-Path $OutDir "HomePodCast-$Version-x64.msix"
Write-Host "HomePodCast $Version -> package version $msixVersion -> $msix"
Write-Host "makeappx: $makeappx"

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $layout, $priRoot, $OutDir | Out-Null

# 1. Self-contained publish (a folder, not single-file: the package is compressed anyway, and a folder starts faster).
$publishArgs = @(
    'publish', (Join-Path $repo 'src\HomePodCast.csproj')
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true'
    "-p:Version=$Version"
    "-p:OutputPath=$(Join-Path $work 'bin')\"
    # Intermediates stay in src\obj (a win-x64 subfolder of their own). Moving BaseIntermediateOutputPath out of src
    # let the default compile glob pick up src\obj's generated AssemblyInfo files from an earlier build (CI: duplicate
    # attribute errors right after the framework-dependent build).
    '-o', $layout
)
dotnet @publishArgs
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
if (-not (Test-Path (Join-Path $layout 'HomePodCast.exe'))) { throw "dotnet publish wrote no HomePodCast.exe to $layout" }

# 2. Logos, license and the manifest.
Copy-Item (Join-Path $PSScriptRoot 'Assets') $layout -Recurse
Copy-Item (Join-Path $repo 'LICENSE') $layout
function Escape([string] $s) { [System.Security.SecurityElement]::Escape($s) }
$manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw -Encoding utf8).
    Replace('__IDENTITY_NAME__', (Escape $IdentityName)).
    Replace('__PUBLISHER__', (Escape $Publisher)).
    Replace('__VERSION__', $msixVersion).
    Replace('__PUBLISHER_DISPLAY_NAME__', (Escape $PublisherDisplayName)).
    Replace('__DISPLAY_NAME__', (Escape $DisplayName))
[xml] $manifest | Out-Null # well-formed
$manifestPath = Join-Path $layout 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.UTF8Encoding]::new($false))

# 3. resources.pri (one file: priconfig.xml has no resource-package split), indexed from a folder holding only the
# manifest and the logos; the layout's hundreds of runtime files are no resources. Resource names are relative to
# that folder, so they match the layout's Assets\ paths. The manifest gives the resource map its name.
Copy-Item $manifestPath $priRoot
Copy-Item (Join-Path $PSScriptRoot 'Assets') $priRoot -Recurse
$consoleEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::Unicode # makepri writes UTF-16
try {
    & $makepri new /pr $priRoot /cf (Join-Path $PSScriptRoot 'priconfig.xml') /mn (Join-Path $priRoot 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o
    if ($LASTEXITCODE) { throw 'makepri new failed' }
}
finally {
    [Console]::OutputEncoding = $consoleEncoding
}
$extraPri = @(Get-ChildItem $layout -Filter 'resources.*.pri')
if ($extraPri.Count) { throw "makepri split the resources ($($extraPri.Name -join ', ')): check priconfig.xml" }

# The Store resolves the manifest's logos (Assets\Name.png) through resources.pri, so each must be the named resource
# Files/Assets/Name.png there. 2026-10-13: they were Files/Name.png; Windows installed that package without a word
# (msix-e2e passed), Partner Center refused it: "can't find the images specified in appxManifest.xml".
$priDump = Join-Path $work 'resources.pri.xml'
& $makepri dump /if (Join-Path $layout 'resources.pri') /of $priDump /o | Out-Null
if ($LASTEXITCODE) { throw 'makepri dump failed' }
$priNames = @(([xml][System.IO.File]::ReadAllText($priDump, [System.Text.Encoding]::UTF8)).SelectNodes('//NamedResource') |
    ForEach-Object { $_.uri -replace '^ms-resource://[^/]+/', '' })
$logos = @([regex]::Matches($manifest, '"(Assets\\[^"]+\.png)"') | ForEach-Object { 'Files/' + ($_.Groups[1].Value -replace '\\', '/') } |
    Sort-Object -Unique)
$missing = @($logos | Where-Object { $_ -notin $priNames })
if ($logos.Count -eq 0 -or $missing.Count) { throw "resources.pri lacks $($missing -join ', ') (it has $($priNames -join ', '))" }
Write-Host "resources.pri resolves the manifest's $($logos.Count) logos"

# 4. Pack; makeappx validates the manifest and the files it references.
& $makeappx pack /d $layout /p $msix /o | Where-Object { $_ -notmatch '^Processing ".*" as a payload file' } # one line per file
if ($LASTEXITCODE) { throw 'makeappx pack failed' }

$size = (Get-Item $msix).Length
Write-Host ("{0}: {1:N1} MB, {2} files" -f $msix, ($size / 1MB), @(Get-ChildItem $layout -Recurse -File).Count)
$msix
