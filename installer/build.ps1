<#
.SYNOPSIS
  Builds the HomePodCast MSI (WiX Toolset v5, restored as a local dotnet tool).

.DESCRIPTION
  Expects the framework-dependent single-file publish output, e.g.
    dotnet publish src/HomePodCast.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/HomePodCast
  and writes <OutDir>\HomePodCast-<Version>-x64.msi.

  MSI version rule (ProductVersion must be numeric, major.minor.build[.revision]):
    1.2.3            -> 1.2.3
    1.2.3-<anything> -> 1.2.3.<Build>   (prerelease tags, branch and PR builds; Build = CI run number)
  Windows Installer compares only the first three fields, so builds that differ only in the fourth
  replace each other (MajorUpgrade AllowSameVersionUpgrades); a lower major.minor.build is refused.

.EXAMPLE
  ./installer/build.ps1 -Version 0.2.0-local.1 -Validate
#>
param(
    # The CI version (build.yml "Version" step), e.g. 1.2.3, 0.2.0-beta.1, 0.2.0-dev.57.
    [Parameter(Mandatory)] [string] $Version,
    # Fourth MSI version field for prerelease versions (GITHUB_RUN_NUMBER in CI).
    [ValidateRange(0, 65535)] [int] $Build = 0,
    [string] $PublishDir = 'publish/HomePodCast',
    [string] $OutDir = 'publish',
    # Run the Windows Installer ICE validation on the result.
    [switch] $Validate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Pinned to WiX v5: v6 and later need an extra EULA (Open Source Maintenance Fee) to be accepted.
$WixExtensions = @(
    'WixToolset.UI.wixext/5.0.2'
    'WixToolset.Firewall.wixext/5.0.2'
    'WixToolset.Util.wixext/5.0.2'
)

function ConvertTo-MsiVersion([string] $v, [int] $build) {
    if ($v -notmatch '^(\d+)\.(\d+)\.(\d+)(-.+)?$') { throw "version '$v' is not X.Y.Z or X.Y.Z-suffix" }
    $major, $minor, $patch = [int]$Matches[1], [int]$Matches[2], [int]$Matches[3]
    if ($major -gt 255 -or $minor -gt 255 -or $patch -gt 65535) { throw "version '$v' is out of the MSI range (255.255.65535)" }
    if ($Matches[4]) { "$major.$minor.$patch.$build" } else { "$major.$minor.$patch" }
}

# Plain-text LICENSE -> RTF for the license dialog: paragraphs are re-flowed so they wrap to the dialog
# width, tab-indented headings are centered, and indented sample notices keep their line breaks.
function ConvertTo-Rtf([string] $text) {
    function Escape([string] $s) {
        $sb = [System.Text.StringBuilder]::new()
        foreach ($c in $s.ToCharArray()) {
            if ($c -eq '\' -or $c -eq '{' -or $c -eq '}') { [void]$sb.Append('\').Append($c) }
            elseif ([int]$c -gt 127) { [void]$sb.Append("\u$(if ([int]$c -gt 32767) { [int]$c - 65536 } else { [int]$c })?") }
            else { [void]$sb.Append($c) }
        }
        $sb.ToString()
    }
    $out = [System.Text.StringBuilder]::new()
    [void]$out.Append('{\rtf1\ansi\ansicpg1252\deff0\uc1{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}\viewkind4\fs16' + "`n")
    $blocks = ($text -replace "`r", '') -split "`n(?:[ `t]*`n)+"
    foreach ($block in $blocks) {
        $lines = @($block -split "`n" | Where-Object { $_.Trim() })
        if (-not $lines) { continue }
        if ($lines[0] -match '^\t') {
            $body = ($lines | ForEach-Object { Escape $_.Trim() }) -join '\line '
            [void]$out.Append("\pard\qc\sa120\b $body\b0\par`n")
        }
        elseif (@($lines | Where-Object { $_ -notmatch '^ {4}' }).Count -eq 0 -and $lines[0] -notmatch '^\s+[a-z]\)') {
            $body = ($lines | ForEach-Object { Escape $_.Trim() }) -join '\line '
            [void]$out.Append("\pard\li360\sa120 $body\par`n")
        }
        elseif ($lines.Count -eq 1 -and $lines[0] -match '^\s*\d+\. \S') {
            [void]$out.Append("\pard\sa120\b $(Escape $lines[0].Trim())\b0\par`n") # section title
        }
        else {
            $indent = if ($lines[0] -match '^ {4}') { '\li360' } else { '' }
            $body = Escape ((($lines | ForEach-Object { $_.Trim() }) -join ' ') -replace '  +', ' ')
            [void]$out.Append("\pard$indent\sa120 $body\par`n")
        }
    }
    [void]$out.Append('}')
    $out.ToString()
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$PublishDir = [System.IO.Path]::GetFullPath($PublishDir, $repo)
$OutDir = [System.IO.Path]::GetFullPath($OutDir, $repo)
$exe = Join-Path $PublishDir 'HomePodCast.exe'
if (-not (Test-Path $exe)) { throw "$exe not found: run dotnet publish first (see the comment at the top of this script)" }

$msiVersion = ConvertTo-MsiVersion $Version $Build
$obj = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force $obj, $OutDir | Out-Null
$rtf = Join-Path $obj 'License.rtf'
[System.IO.File]::WriteAllText($rtf, (ConvertTo-Rtf (Get-Content (Join-Path $repo 'LICENSE') -Raw)), [System.Text.Encoding]::ASCII)
$msi = Join-Path $OutDir "HomePodCast-$Version-x64.msi"
$pdb = Join-Path $obj 'HomePodCast.wixpdb'
Write-Host "HomePodCast $Version -> MSI version $msiVersion -> $msi"

# Extensions are cached in installer\.wix (wix looks there when run from this folder).
Push-Location $PSScriptRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }
    foreach ($ext in $WixExtensions) {
        dotnet wix extension add $ext
        if ($LASTEXITCODE) { throw "wix extension add $ext failed" }
    }
    $wixArgs = @(
        'build', 'HomePodCast.wxs', '-arch', 'x64'
        $WixExtensions | ForEach-Object { '-ext', $_ }
        '-d', "ProductVersion=$msiVersion"
        '-d', "DisplayVersion=$Version"
        '-d', "PublishDir=$PublishDir"
        '-d', "RepoDir=$repo"
        '-d', "LicenseRtf=$rtf"
        '-intermediatefolder', $obj
        '-pdb', $pdb
        '-o', $msi
    )
    dotnet wix @wixArgs
    if ($LASTEXITCODE) { throw 'wix build failed' }
    if ($Validate) {
        # ICE61 (warning) is expected: AllowSameVersionUpgrades lets a build replace one with the same version.
        dotnet wix msi validate -pdb $pdb -intermediateFolder (Join-Path $obj 'validate') $msi
        if ($LASTEXITCODE) { throw 'ICE validation failed' }
    }
}
finally {
    Pop-Location
}

$msi
