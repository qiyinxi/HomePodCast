<#
.SYNOPSIS
  Screen-capture measurement of the delay engine's picture delay.

  1. serves the repo root with `python -m http.server` on 127.0.0.1 (so ../../extension/src resolves)
  2. opens testpage.html?delay=<ms> in a NEW Chrome instance with a throwaway --user-data-dir in %TEMP%
  3. waits for the page, then -Warmup seconds, and records the window with ffmpeg gdigrab at 60 fps
  4. closes that Chrome instance and the server, deletes the temp profile, runs measure.py

  Output (next to this script): capture_<ms>.mp4, capture_<ms>.json (results),
  capture_<ms>_rows.png (sample points), capture_<ms>_page.png (one full captured frame: check the
  engine/stats line under the videos says HPCDelay, not STUB).

.PARAMETER Method
  title   : gdigrab -i title="HPC AVSYNC TEST - Google Chrome" (window DC; may come out black for
            GPU-composited Chrome windows)
  desktop : gdigrab of the screen region under the window (window is brought to the front and must
            stay uncovered)
  ddagrab : Desktop Duplication of monitor -Output (window must be on that monitor, uncovered)
  auto    : title, and if measure.py cannot decode that recording, desktop (default)

.EXAMPLE
  .\capture.ps1 -Delay 141
  .\capture.ps1 -Delay 0 -Method desktop
#>
param(
    [int]$Delay = 141,
    [double]$Seconds = 15,
    [double]$Warmup = 5,
    [ValidateSet('auto', 'title', 'desktop', 'ddagrab')][string]$Method = 'auto',
    [int]$Port = 0,                      # 0 = pick a free port
    [int]$Output = 0,                    # ddagrab monitor index
    [string]$Python = 'C:\Users\qiyin\HomePodCast\venv\Scripts\python.exe',
    [string]$Chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [string]$Title = 'HPC AVSYNC TEST',
    [switch]$KeepProfile
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = (Resolve-Path (Join-Path $here '..\..')).Path
$base = Join-Path $here "capture_$Delay"
$out = "$base.mp4"

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class HpcWin {
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public class Win { public IntPtr Handle; public uint Pid; public string Title; }
    public static List<Win> Find(string prefix) {
        var list = new List<Win>();
        EnumWindows((h, p) => {
            if (IsWindowVisible(h)) {
                var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
                var t = sb.ToString();
                if (t.StartsWith(prefix)) { uint pid; GetWindowThreadProcessId(h, out pid); list.Add(new Win { Handle = h, Pid = pid, Title = t }); }
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
    // Window bounds without the invisible resize border, in physical pixels.
    public static RECT Bounds(IntPtr h) {
        RECT r; DwmGetWindowAttribute(h, 9, out r, Marshal.SizeOf(typeof(RECT))); return r;
    }
}
'@
[void][HpcWin]::SetThreadDpiAwarenessContext([IntPtr]-4)   # per-monitor v2: physical pixels

function Stop-Tree([int]$ProcId) {
    if ($ProcId -and (Get-Process -Id $ProcId -ErrorAction SilentlyContinue)) {
        & taskkill.exe /PID $ProcId /T /F > $null 2> $null
    }
}

function Invoke-Record([string]$How, $Win) {
    $enc = @('-t', "$Seconds", '-vf', 'crop=trunc(iw/2)*2:trunc(ih/2)*2', '-fps_mode', 'passthrough',
        '-c:v', 'libx264', '-preset', 'ultrafast', '-crf', '16', '-pix_fmt', 'yuv420p', $out)
    $r = [HpcWin]::Bounds($Win.Handle)
    # clip to the virtual screen (SM_X/Y/CX/CYVIRTUALSCREEN)
    $vx = [HpcWin]::GetSystemMetrics(76); $vy = [HpcWin]::GetSystemMetrics(77)
    $x0 = [Math]::Max($r.L, $vx); $y0 = [Math]::Max($r.T, $vy)
    $x1 = [Math]::Min($r.R, $vx + [HpcWin]::GetSystemMetrics(78)); $y1 = [Math]::Min($r.B, $vy + [HpcWin]::GetSystemMetrics(79))
    $w = ($x1 - $x0) -band -2; $h = ($y1 - $y0) -band -2
    if ($How -ne 'title') {
        [void][HpcWin]::ShowWindow($Win.Handle, 9)            # SW_RESTORE
        [void][HpcWin]::SetForegroundWindow($Win.Handle)
        Start-Sleep -Milliseconds 500
    }
    switch ($How) {
        'title' {
            Write-Host "recording $Seconds s: gdigrab title=`"$($Win.Title)`""
            $in = @('-f', 'gdigrab', '-framerate', '60', '-draw_mouse', '0', '-i', "title=$($Win.Title)")
        }
        'desktop' {
            Write-Host "recording $Seconds s: gdigrab desktop region ${w}x${h} at $x0,$y0 (window must stay uncovered)"
            $in = @('-f', 'gdigrab', '-framerate', '60', '-draw_mouse', '0', '-offset_x', "$x0", '-offset_y', "$y0",
                '-video_size', "${w}x${h}", '-i', 'desktop')
        }
        'ddagrab' {
            Write-Host "recording $Seconds s: ddagrab output $Output region ${w}x${h} at $x0,$y0 (offsets assume that monitor's origin is 0,0)"
            $in = @('-f', 'lavfi', '-i', "ddagrab=output_idx=${Output}:framerate=60:draw_mouse=0:offset_x=${x0}:offset_y=${y0}:video_size=${w}x${h},hwdownload,format=bgra")
        }
    }
    & ffmpeg -y -hide_banner -loglevel warning @in @enc
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $out)) { throw "ffmpeg ($How) failed" }
}

if (-not (Test-Path (Join-Path $here 'testvideo.mp4'))) {
    Write-Host 'testvideo.mp4 missing, generating it...'
    & $Python (Join-Path $here 'gen_testvideo.py'); if ($LASTEXITCODE -ne 0) { throw 'gen_testvideo.py failed' }
}
if (-not (Test-Path (Join-Path $root 'extension\src\delay-engine.js'))) {
    Write-Warning 'extension\src\delay-engine.js not found: the page will use its built-in no-delay stub'
}
if ($Port -eq 0) {
    $l = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $l.Start(); $Port = $l.LocalEndpoint.Port; $l.Stop()
}
$work = Join-Path $env:TEMP ('hpc_avsync_' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$profileDir = Join-Path $work 'profile'
New-Item -ItemType Directory -Force $profileDir | Out-Null
$url = "http://127.0.0.1:$Port/tools/avsync/testpage.html?delay=$Delay"
$server = $null; $browser = $null; $rc = 1
try {
    $server = Start-Process -FilePath $Python -PassThru -WindowStyle Hidden `
        -ArgumentList @('-m', 'http.server', "$Port", '--bind', '127.0.0.1', '--directory', "`"$root`"") `
        -RedirectStandardError (Join-Path $work 'server.log') -RedirectStandardOutput (Join-Path $work 'server.out')
    $deadline = (Get-Date).AddSeconds(10)
    while ($true) {
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch {
            if ((Get-Date) -gt $deadline) { throw "http server on port $Port did not come up" }
            Start-Sleep -Milliseconds 200
        }
    }
    Write-Host "serving $root on http://127.0.0.1:$Port"

    $chromeArgs = @(
        "--user-data-dir=`"$profileDir`"", '--new-window', '--window-size=1400,600', '--window-position=40,40',
        '--autoplay-policy=no-user-gesture-required', '--no-first-run', '--no-default-browser-check',
        '--disable-search-engine-choice-screen', '--disable-sync', '--hide-crash-restore-bubble',
        '--disable-backgrounding-occluded-windows', '--disable-renderer-backgrounding',
        '--disable-background-timer-throttling', '--disable-features=CalculateNativeWinOcclusion,Translate',
        $url)
    $browser = Start-Process -FilePath $Chrome -ArgumentList $chromeArgs -PassThru
    Write-Host "chrome pid $($browser.Id), profile $profileDir"

    $deadline = (Get-Date).AddSeconds(20); $win = $null
    while (-not $win) {
        $all = [HpcWin]::Find($Title)
        $win = ($all | Where-Object { $_.Pid -eq $browser.Id } | Select-Object -First 1)
        if (-not $win) { $win = $all | Select-Object -First 1 }
        if (-not $win) {
            if ((Get-Date) -gt $deadline) { throw "no window titled '$Title*' appeared" }
            Start-Sleep -Milliseconds 250
        }
    }
    $b = [HpcWin]::Bounds($win.Handle)
    Write-Host "window: `"$($win.Title)`"  $($b.R - $b.L)x$($b.B - $b.T) at $($b.L),$($b.T) (physical px)"
    Write-Host "warming up $Warmup s..."
    Start-Sleep -Seconds $Warmup

    $methods = if ($Method -eq 'auto') { @('title', 'desktop') } else { @($Method) }
    foreach ($m in $methods) {
        $last = ($m -eq $methods[-1])
        try { Invoke-Record $m $win } catch {
            if ($last) { throw }
            Write-Host "$m recording failed ($_); trying the next method"; continue
        }
        if ($last) { break }
        & $Python (Join-Path $here 'measure.py') $out --quiet | Out-Null
        if ($LASTEXITCODE -eq 0) { break }
        Write-Host "the $m recording could not be decoded or looked frozen (exit $LASTEXITCODE); trying the next method"
    }
}
finally {
    if ($browser) {
        try { $browser.Refresh(); [void]$browser.CloseMainWindow(); [void]$browser.WaitForExit(3000) } catch {}
        Stop-Tree $browser.Id
    }
    if ($server) { Stop-Tree $server.Id }
    if (-not $KeepProfile) {
        Start-Sleep -Milliseconds 500
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
}

& ffmpeg -y -hide_banner -loglevel error -ss 1 -i $out -frames:v 1 "${base}_page.png"
& $Python (Join-Path $here 'measure.py') $out --debug-png "${base}_rows.png" --json "$base.json"
$rc = $LASTEXITCODE
Write-Host "files: $out, $base.json, ${base}_rows.png, ${base}_page.png"
if ($rc -ne 0) { Write-Host 'measurement failed: look at the _page.png frame; try -Method desktop or -Method ddagrab' }
exit $rc
