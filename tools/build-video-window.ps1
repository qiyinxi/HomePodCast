# The synchronized viewing window (modules/VideoWindow, experimental since 1.1): published into its own folder, which
# the build job zips as HomePodCast-video-window-<version>.zip. The version must be the app's: the module warns when the
# app it follows reports another one.
param([string]$Version = '1.0.0', [string]$OutDir = 'publish/video-window')
$ErrorActionPreference = 'Stop'
dotnet publish modules/VideoWindow/VideoWindow.csproj -c Release -r win-x64 --self-contained false -p:Version=$Version -o $OutDir
if ($LASTEXITCODE) { throw 'Viewing module publish failed' }
Copy-Item LICENSE, docs/VIDEO_WINDOW.md -Destination $OutDir
# Its GPU path on WARP: known pictures from 32 x 16 to 8K through the WinRT surface interop and the scaler, read back.
# No window, picker or capture session.
dotnet (Join-Path $OutDir 'HomePodCast.VideoWindow.dll') --self-test
if ($LASTEXITCODE) { throw 'Viewing module self-test failed' }
