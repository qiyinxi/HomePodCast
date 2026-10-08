<#
.SYNOPSIS
  Builds the HomePodCast MSI (WiX Toolset v5, restored as a local dotnet tool).

.DESCRIPTION
  Expects the framework-dependent single-file publish output, e.g.
    dotnet publish src/HomePodCast.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish/HomePodCast
  and writes <OutDir>\HomePodCast-<Version>-x64.msi, one package whose setup wizard follows the Windows
  UI language:
    1. builds HomePodCast.wxs once per culture in $Cultures (-culture <c> -loc i18n\<c>.wxl) into installer\obj\<c>;
    2. makes a language transform from the en-US package to each other culture's package (wix msi transform -t language);
    3. copies the en-US package to the output and embeds each transform as a sub-storage named by its LCID,
       then lists all LCIDs in the summary information (Template "x64;1033,2052,..."). Windows Installer
       applies the transform that matches the user's UI language; any other language gets English.
  Step 3 and the -Validate checks use the WindowsInstaller.Installer COM object (no admin rights needed).

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
    # Run the Windows Installer ICE validation on the result and on every culture's package, and check
    # that each embedded language transform turns the result into exactly that culture's package.
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

# Wizard languages: culture -> Windows LCID (the name of the embedded transform). The first one is the
# base package. Each culture needs i18n\<culture>.wxl for our own strings; the stock wizard strings
# come from WixToolset.UI.wixext, which has them for all of these.
$Cultures = [ordered]@{
    'en-US' = 1033
    'zh-CN' = 2052
    'zh-TW' = 1028
    'ja-JP' = 1041
    'de-DE' = 1031
    'fr-FR' = 1036
    'es-ES' = 3082
    'it-IT' = 1040
    'nl-NL' = 1043
    'pl-PL' = 1045
    'pt-PT' = 2070
    'sv-SE' = 1053
    'da-DK' = 1030
    'nb-NO' = 1044
    'fi-FI' = 1035
}
$BaseCulture = @($Cultures.Keys)[0]

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

# Windows Installer COM objects (WindowsInstaller.Installer, no admin rights needed). A database stays open
# until every object made from it is released, so release them explicitly, last one first.
function Close-Com {
    foreach ($o in $args) {
        if ($null -ne $o -and [Runtime.InteropServices.Marshal]::IsComObject($o)) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($o) }
    }
}

# Embeds each transform as a sub-storage named by its LCID (what WiSubStg.vbs from the Windows SDK does)
# and lists the LCIDs in the Template summary property, base language first (what WiLangId.vbs does).
function Add-LanguageTransforms([string] $msiPath, [System.Collections.IDictionary] $transforms, [int[]] $languages) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $view = $summary = $null
    try {
        $db = $installer.OpenDatabase($msiPath, 1) # msiOpenDatabaseModeTransact
        $view = $db.OpenView('SELECT `Name`, `Data` FROM `_Storages`')
        [void]$view.Execute() # COM methods without a result still write $null to the output
        foreach ($lcid in $transforms.Keys) {
            $record = $installer.CreateRecord(2)
            $record.StringData(1) = "$lcid"
            [void]$record.SetStream(2, $transforms[$lcid])
            [void]$view.Modify(3, $record) # msiViewModifyAssign
            Close-Com $record
        }
        [void]$view.Close()
        $summary = $db.SummaryInformation(1)
        $platform = ($summary.Property(7) -split ';')[0] # PID_TEMPLATE, "x64;1033"
        $summary.Property(7) = "$platform;$($languages -join ',')"
        [void]$summary.Persist()
        [void]$db.Commit()
    }
    finally {
        Close-Com $summary $view $db $installer
    }
}

function Get-MsiProperty($db, [string] $name) {
    $view = $db.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$name'")
    try {
        [void]$view.Execute() # COM methods without a result still write $null to the output
        $record = $view.Fetch()
        if ($record) { $record.StringData(1); Close-Com $record }
        [void]$view.Close()
    }
    finally {
        Close-Com $view
    }
}

# Applies the embedded transform ":<lcid>" to a copy of the multilingual package, the way Windows Installer
# does at install time (with the error conditions stored in the transform), and checks that the result has
# exactly the tables of that culture's own package and our finish-dialog checkbox text from its .wxl.
function Test-LanguageTransform([string] $msiPath, [int] $lcid, [string] $mstPath, [string] $culturePackage, [string] $expectedLaunchText, [string] $workDir) {
    $copy = Join-Path $workDir "$lcid.msi"
    $diff = Join-Path $workDir "$lcid-differences.mst"
    Copy-Item $msiPath $copy -Force
    Remove-Item $diff -ErrorAction SilentlyContinue
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $summary = $db = $expected = $null
    try {
        $summary = $installer.SummaryInformation($mstPath, 0)
        $errorConditions = [int]$summary.Property(16) -band 0xFFFF # PID_CHARCOUNT, low word
        $db = $installer.OpenDatabase($copy, 1) # msiOpenDatabaseModeTransact
        [void]$db.ApplyTransform(":$lcid", $errorConditions)
        [void]$db.Commit()
        Close-Com $db
        $db = $installer.OpenDatabase($copy, 0) # msiOpenDatabaseModeReadOnly
        $expected = $installer.OpenDatabase($culturePackage, 0)
        # GenerateTransform returns false when the two databases are identical.
        if ($expected.GenerateTransform($db, $diff)) { throw "transform :$lcid does not reproduce $culturePackage (differences: $diff)" }
        $launchText = Get-MsiProperty $db 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'
        if ($launchText -cne $expectedLaunchText) { throw "transform :${lcid}: finish checkbox text is '$launchText', expected '$expectedLaunchText'" }
    }
    finally {
        Close-Com $expected $db $summary $installer
    }
}

# The multilingual package itself is the base culture: its language list and its own checkbox text.
function Test-BasePackage([string] $msiPath, [string] $expectedTemplate, [string] $expectedLaunchText) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $db = $summary = $null
    try {
        $db = $installer.OpenDatabase($msiPath, 0)
        $summary = $db.SummaryInformation(0)
        $template = $summary.Property(7)
        if ($template -ne $expectedTemplate) { throw "Template summary property is '$template', expected '$expectedTemplate'" }
        $launchText = Get-MsiProperty $db 'WIXUI_EXITDIALOGOPTIONALCHECKBOXTEXT'
        if ($launchText -cne $expectedLaunchText) { throw "finish checkbox text is '$launchText', expected '$expectedLaunchText'" }
    }
    finally {
        Close-Com $summary $db $installer
    }
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
# One product code for all cultures of this build: a language transform must not change the product.
$productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()
Write-Host "HomePodCast $Version -> MSI version $msiVersion -> $msi ($($Cultures.Count) languages)"

function Get-CulturePath([string] $culture, [string] $file) { Join-Path (Join-Path $obj $culture) $file }

# Extensions are cached in installer\.wix (wix looks there when run from this folder).
Push-Location $PSScriptRoot
try {
    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore failed' }
    foreach ($ext in $WixExtensions) {
        dotnet wix extension add $ext
        if ($LASTEXITCODE) { throw "wix extension add $ext failed" }
    }

    foreach ($culture in $Cultures.Keys) {
        $wxl = Join-Path 'i18n' "$culture.wxl"
        if (-not (Test-Path $wxl)) { throw "installer\$wxl not found" }
        $wixArgs = @(
            'build', 'HomePodCast.wxs', '-arch', 'x64'
            $WixExtensions | ForEach-Object { '-ext', $_ }
            '-culture', $culture, '-loc', $wxl
            '-d', "ProductVersion=$msiVersion"
            '-d', "DisplayVersion=$Version"
            '-d', "ProductCode=$productCode"
            '-d', "ProductLanguage=$($Cultures[$culture])"
            '-d', "PublishDir=$PublishDir"
            '-d', "RepoDir=$repo"
            '-d', "LicenseRtf=$rtf"
            '-intermediatefolder', (Join-Path $obj $culture)
            '-pdb', (Get-CulturePath $culture 'HomePodCast.wixpdb')
            '-o', (Get-CulturePath $culture 'HomePodCast.msi')
        )
        dotnet wix @wixArgs
        if ($LASTEXITCODE) { throw "wix build ($culture) failed" }
    }

    $basePackage = Get-CulturePath $BaseCulture 'HomePodCast.msi'
    $transforms = [ordered]@{}
    foreach ($culture in @($Cultures.Keys | Where-Object { $_ -ne $BaseCulture })) {
        $mst = Join-Path $obj "$culture.mst"
        dotnet wix msi transform -t language -intermediateFolder (Join-Path $obj 'transforms') -o $mst $basePackage (Get-CulturePath $culture 'HomePodCast.msi')
        if ($LASTEXITCODE) { throw "wix msi transform ($culture) failed" }
        $transforms["$($Cultures[$culture])"] = $mst # string keys: an int would index the ordered dictionary by position
    }

    Copy-Item $basePackage $msi -Force
    Add-LanguageTransforms $msi $transforms ([int[]]@($Cultures.Values))

    if ($Validate) {
        # ICE61 (warning, once per package) is expected: AllowSameVersionUpgrades lets a build replace one with the same version.
        foreach ($culture in $Cultures.Keys) {
            $package = if ($culture -eq $BaseCulture) { $msi } else { Get-CulturePath $culture 'HomePodCast.msi' }
            dotnet wix msi validate -pdb (Get-CulturePath $culture 'HomePodCast.wixpdb') -intermediateFolder (Get-CulturePath $culture 'validate') $package
            if ($LASTEXITCODE) { throw "ICE validation ($culture) failed" }
        }
        $check = Join-Path $obj 'check'
        New-Item -ItemType Directory -Force $check | Out-Null
        foreach ($culture in $Cultures.Keys) {
            $lcid = $Cultures[$culture]
            [xml] $loc = Get-Content (Join-Path 'i18n' "$culture.wxl") -Raw -Encoding utf8
            $launchText = @($loc.WixLocalization.String | Where-Object { $_.GetAttribute('Id') -eq 'LaunchApp' })[0].GetAttribute('Value')
            if ($culture -eq $BaseCulture) {
                Test-BasePackage $msi "x64;$(@($Cultures.Values) -join ',')" $launchText
            }
            else {
                Test-LanguageTransform $msi $lcid $transforms["$lcid"] (Get-CulturePath $culture 'HomePodCast.msi') $launchText $check
            }
            Write-Host "Language check $culture ($lcid): ok"
        }
    }
}
finally {
    Pop-Location
}

$msi
