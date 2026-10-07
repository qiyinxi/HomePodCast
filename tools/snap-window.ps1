# Captures only HomePodCast's own main window (PrintWindow, so overlapping windows are not included).
param([string]$Out = "$env:TEMP\hpc_snap.png", [string]$Process = 'HomePodCast', [string]$Title = '')

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Snap {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@

[void][Snap]::SetThreadDpiAwarenessContext([IntPtr]-4) # per-monitor v2: physical pixels
$h = if ($Title) { [Snap]::FindWindow($null, $Title) } else { (Get-Process $Process -ErrorAction Stop | Select-Object -First 1).MainWindowHandle }
if ($h -eq [IntPtr]::Zero) { Write-Output 'window not found'; exit 1 }
$r = New-Object Snap+RECT; [void][Snap]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L; $hgt = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc(); [void][Snap]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Output "$Out ${w}x${hgt}"
