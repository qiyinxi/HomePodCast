<#
Builds the optional music separation module (docs/MUSIC_STEMS.md) into -Output: the worker, the vocal branch of the
pinned StemgenRT model, the licenses (HomePodCast's GPL, StemgenRT's and ONNX Runtime's) and NOTICE.txt; then checks it
against the publisher's independent PyTorch reference and benchmarks it.

The upstream files are pinned in modules/StemWorker/upstream.json (one commit, SHA-256s). They are kept in -Cache and
checked on every use; only what is missing there is downloaded. CI keeps that folder in actions/cache (build.yml, job
stem-module), so a run with the cache downloads nothing from upstream and installs nothing from PyPI.

Exit code 3: upstream (StemgenRT on GitHub, its Git LFS, or PyPI for the onnx tool) could not be reached or served
something else, so the module can't be built now. Anything else that goes wrong throws, a file of the right size with
the wrong SHA-256 included.

Developer use:
  ./tools/build-stem-module.ps1 -InstallOnnx
  $env:HOMEPODCAST_STEM_MODULE = (Resolve-Path publish/stem-module).Path
  dotnet test tests/HomePodCast.Tests --filter FullyQualifiedName~StemModuleTests
Extracting the vocal branch needs Python with onnx 1.20.1 (-InstallOnnx runs pip for it); once the extracted model is in
the cache, no Python is needed.
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '../publish/stem-module'),
    [string]$Cache = (Join-Path $PSScriptRoot '../.stem-cache'),
    [switch]$InstallOnnx
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$UpstreamUnavailable = 3
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$pin = Get-Content (Join-Path $root 'modules/StemWorker/upstream.json') -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Cache | Out-Null

function Test-Hash([string] $path, [string] $sha256) {
    (Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $sha256
}

# 'ok', 'wrong' (not what upstream should serve: an error page, an LFS pointer...) or 'tampered' (the right size with the
# wrong SHA-256).
function Test-Pinned([string] $path, $file) {
    if (-not (Test-Path -LiteralPath $path)) { return 'wrong' }
    if ($file.PSObject.Properties['contains']) { return $(if ((Get-Content -LiteralPath $path -Raw) -match [regex]::Escape($file.contains)) { 'ok' } else { 'wrong' }) }
    if ($file.PSObject.Properties['size'] -and (Get-Item -LiteralPath $path).Length -ne $file.size) { return 'wrong' }
    if (Test-Hash $path $file.sha256) { 'ok' } elseif ($file.PSObject.Properties['size']) { 'tampered' } else { 'wrong' }
}

# Puts the pinned upstream file into the cache unless it is there already; $false when upstream can't provide it now.
function Get-Upstream([string] $url, [string] $path, $file) {
    if ((Test-Pinned $path $file) -eq 'ok') { Write-Host "cached: $(Split-Path -Leaf $path)"; return $true }
    $pending = "$path.download"
    try { Invoke-WebRequest $url -OutFile $pending -TimeoutSec 600 }
    catch [System.Net.Http.HttpRequestException], [System.Threading.Tasks.TaskCanceledException], [System.IO.IOException] {
        Remove-Item -LiteralPath $pending -ErrorAction SilentlyContinue
        Write-Warning "upstream not reachable: $url ($($_.Exception.Message))"
        return $false
    }
    switch (Test-Pinned $pending $file) {
        'ok' { Move-Item -LiteralPath $pending -Destination $path -Force; Write-Host "downloaded: $url"; return $true }
        'tampered' { Remove-Item -LiteralPath $pending; throw "$url has the pinned size but not the pinned SHA-256 (modules/StemWorker/upstream.json)" }
        default {
            $length = (Get-Item -LiteralPath $pending).Length
            Remove-Item -LiteralPath $pending
            Write-Warning "upstream served something else for $url ($length bytes)"
            return $false
        }
    }
}

$raw = "https://raw.githubusercontent.com/$($pin.repository)/$($pin.commit)"
$lfs = "https://media.githubusercontent.com/media/$($pin.repository)/$($pin.commit)"
$fixture = Join-Path $Cache 'independent-pytorch.bin'
$license = Join-Path $Cache 'StemgenRT-LICENSE.txt'
$vocals = Join-Path $Cache 'vocals.onnx'
$available = (Get-Upstream "$raw/$($pin.fixture.path)" $fixture $pin.fixture) -and
             (Get-Upstream "$raw/$($pin.license.path)" $license $pin.license)
if ($available -and -not (Test-Hash $vocals $pin.vocals.sha256)) {
    $model = Join-Path $Cache 'model.onnx'
    $available = Get-Upstream "$lfs/$($pin.model.path)" $model $pin.model
    if ($available) {
        & python -c "import onnx, sys; sys.exit(0 if onnx.__version__ == '1.20.1' else 1)" 2>$null
        if ($LASTEXITCODE -and $InstallOnnx) {
            & python -m pip install onnx==1.20.1
            if ($LASTEXITCODE) { Write-Warning 'pip could not install onnx 1.20.1 (PyPI not reachable?)'; $available = $false }
        }
        elseif ($LASTEXITCODE) { throw 'Extracting the vocal model needs Python with onnx 1.20.1: pip install onnx==1.20.1, or run with -InstallOnnx' }
    }
    if ($available) {
        & python (Join-Path $PSScriptRoot 'extract-vocal-model.py') $model $vocals
        if ($LASTEXITCODE) { throw 'Vocal model extraction failed' }
        if (-not (Test-Hash $vocals $pin.vocals.sha256)) { Remove-Item -LiteralPath $vocals; throw 'Pruned vocal model SHA-256 mismatch' }
    }
}
if (-not $available) {
    Write-Warning 'The music separation module was not built: upstream could not be reached, and the cache does not have its files.'
    exit $UpstreamUnavailable
}

if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Recurse -Force }
$project = Join-Path $root 'modules/StemWorker/StemWorker.csproj'
dotnet publish $project -c Release -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE) { throw 'Stem worker build failed' }
Copy-Item -LiteralPath $vocals, $license -Destination $Output
Copy-Item -LiteralPath (Join-Path $root 'modules/StemWorker/NOTICE.txt') -Destination $Output
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $Output # the module is part of HomePodCast: GPL-3.0-or-later

# ONNX Runtime's license and third-party notices, from the NuGet package the worker was just built with (signed by
# Microsoft and checked by NuGet), so nothing else is downloaded for them.
$ortVersion = ([xml](Get-Content -LiteralPath $project -Raw)).Project.ItemGroup.PackageReference |
    Where-Object Include -eq 'Microsoft.ML.OnnxRuntime' | ForEach-Object Version
$packages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
$ort = Join-Path $packages.Trim() "microsoft.ml.onnxruntime/$ortVersion"
Copy-Item -LiteralPath (Join-Path $ort 'LICENSE') -Destination (Join-Path $Output 'OnnxRuntime-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $ort 'ThirdPartyNotices.txt') -Destination (Join-Path $Output 'OnnxRuntime-NOTICES.txt')

& (Join-Path $Output 'HomePodCast.StemWorker.exe') --verify $fixture
if ($LASTEXITCODE) { throw 'Stem model independent PyTorch parity failed' }
& (Join-Path $Output 'HomePodCast.StemWorker.exe') --benchmark
if ($LASTEXITCODE) { throw 'Stem model benchmark failed' }
