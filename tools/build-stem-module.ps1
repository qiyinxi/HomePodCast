param([string]$Output = (Join-Path $PSScriptRoot '../publish/stem-module'))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$commit = '85e715f1a1982b29204e11595d6fa3f0bde9810a'
$hash = '0A49388DD2A47986AB9A161C8764C22853B4D601C188AC052EB530F076D513C7'
dotnet publish (Join-Path $PSScriptRoot '../modules/StemWorker/StemWorker.csproj') -c Release -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE) { throw 'Stem worker build failed' }
New-Item -ItemType Directory -Force $Output | Out-Null
$model = Join-Path $Output 'source-model.onnx'
if (!(Test-Path $model) -or (Get-FileHash $model).Hash -ne $hash) {
    $pending = "$model.download"
    Invoke-WebRequest "https://media.githubusercontent.com/media/sweetspotsoundsystem/stemgen-rt/$commit/model/model.onnx" -OutFile $pending
    if ((Get-Item $pending).Length -ne 31946241 -or (Get-FileHash $pending).Hash -ne $hash) {
        Remove-Item -LiteralPath $pending
        throw 'Stem model size/SHA-256 mismatch'
    }
    Move-Item -LiteralPath $pending -Destination $model -Force
}
python (Join-Path $PSScriptRoot 'extract-vocal-model.py') $model (Join-Path $Output 'vocals.onnx')
if ($LASTEXITCODE) { throw 'Vocal model extraction failed (requires Python and onnx==1.20.1)' }
if ((Get-FileHash (Join-Path $Output 'vocals.onnx')).Hash -ne '235E795229ECFBB453A295302CAD34D608D520F7FB336E2ED6E1DFD7F0A0E857') {
    throw 'Pruned vocal model SHA-256 mismatch'
}
Remove-Item -LiteralPath $model
Invoke-WebRequest "https://raw.githubusercontent.com/sweetspotsoundsystem/stemgen-rt/$commit/LICENSE.md" -OutFile (Join-Path $Output 'StemgenRT-LICENSE.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.26.0/LICENSE' -OutFile (Join-Path $Output 'OnnxRuntime-LICENSE.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/microsoft/onnxruntime/v1.26.0/ThirdPartyNotices.txt' -OutFile (Join-Path $Output 'OnnxRuntime-NOTICES.txt')
Copy-Item (Join-Path $PSScriptRoot '../modules/StemWorker/NOTICE.txt') $Output
$fixture = Join-Path $Output 'reference.bin'
Invoke-WebRequest "https://raw.githubusercontent.com/sweetspotsoundsystem/stemgen-rt/$commit/test/fixtures/independent-pytorch.bin" -OutFile $fixture
& (Join-Path $Output 'HomePodCast.StemWorker.exe') --verify $fixture
if ($LASTEXITCODE) { throw 'Stem model independent PyTorch parity failed' }
Remove-Item -LiteralPath $fixture
& (Join-Path $Output 'HomePodCast.StemWorker.exe') --benchmark
if ($LASTEXITCODE) { throw 'Stem model benchmark failed' }
