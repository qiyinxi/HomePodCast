param([string]$Version = '1.0.0', [string]$OutDir = 'publish/video-window')
$ErrorActionPreference = 'Stop'
dotnet publish modules/VideoWindow/VideoWindow.csproj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $OutDir
if ($LASTEXITCODE) { throw 'Viewing module publish failed' }
Copy-Item LICENSE, docs/VIDEO_WINDOW.md -Destination $OutDir
# Synthetic WARP texture through WinRT surface/COM readback. No window, picker or capture session.
dotnet (Join-Path $OutDir 'HomePodCast.VideoWindow.dll') --self-test
if ($LASTEXITCODE) { throw 'Viewing module native readback verification failed' }
