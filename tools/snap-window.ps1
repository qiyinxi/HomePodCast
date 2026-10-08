# Captures one of HomePodCast's own top-level windows (PrintWindow, so overlapping windows are not included).
#   snap-window.ps1                 -> main window
#   snap-window.ps1 -Title 混音器   -> window whose title contains the text
#   snap-window.ps1 -List           -> list the process's visible windows
#   snap-window.ps1 -Id 1234        -> that process (when several copies run, e.g. HOMEPODCAST_PROFILE test copies)
param([string]$Out = "$env:TEMP\hpc_snap.png", [string]$Process = 'HomePodCast', [string]$Title = '', [switch]$List, [int]$Id = 0)

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Snap {
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static List<KeyValuePair<IntPtr, string>> Windows(uint pid) {
        var list = new List<KeyValuePair<IntPtr, string>>();
        EnumWindows((h, p) => {
            uint wp; GetWindowThreadProcessId(h, out wp);
            if (wp == pid && IsWindowVisible(h)) {
                var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
                if (sb.Length > 0) list.Add(new KeyValuePair<IntPtr, string>(h, sb.ToString()));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

[void][Snap]::SetThreadDpiAwarenessContext([IntPtr]-4) # per-monitor v2: physical pixels
$proc = if ($Id) { Get-Process -Id $Id -ErrorAction Stop } else { Get-Process $Process -ErrorAction Stop | Select-Object -First 1 }
$wins = [Snap]::Windows([uint32]$proc.Id)
if ($List) { $wins | ForEach-Object { "{0}  {1}" -f $_.Key, $_.Value }; exit 0 }
$h = if ($Title) { ($wins | Where-Object { $_.Value -like "*$Title*" } | Select-Object -First 1).Key } else { $proc.MainWindowHandle }
if (-not $h -or $h -eq [IntPtr]::Zero) { Write-Output 'window not found'; exit 1 }
$r = New-Object Snap+RECT; [void][Snap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); [void][Snap]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output "$Out ${w}x${hgt}"
