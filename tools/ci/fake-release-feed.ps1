<#
.SYNOPSIS
  A fake GitHub release list and its files on http://127.0.0.1:<Port>/, for tools/ci/update-e2e.ps1.

.DESCRIPTION
  GET /<path> answers with the file <Root>\<path> (the query string is ignored), so the e2e script writes the
  release list to <Root>\releases and the MSI to <Root>\download\v<version>\HomePodCast-<version>-x64.msi, and can
  swap a file between requests. Anything outside <Root> or missing is a 404, anything but GET a 405. One log line per
  request on stdout. A plain TcpListener on the loopback address: no http.sys URL reservation, no admin rights, nothing
  reachable from another machine. Runs until it is stopped or -Seconds have passed.

.EXAMPLE
  pwsh -File tools/ci/fake-release-feed.ps1 -Port 8123 -Root $env:TEMP\feed
#>
param(
    [Parameter(Mandatory)] [ValidateRange(1, 65535)] [int] $Port,
    [Parameter(Mandatory)] [string] $Root,
    [int] $Seconds = 1800
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Root = [System.IO.Path]::TrimEndingDirectorySeparator((Resolve-Path -LiteralPath $Root).Path)
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()
[Console]::Out.WriteLine("fake release feed: listening on http://127.0.0.1:$Port/, serving $Root")
[Console]::Out.Flush()

function Send-Response([System.IO.Stream] $stream, [int] $status, [string] $reason, [string] $type, [byte[]] $body) {
    $head = "HTTP/1.1 $status $reason`r`nContent-Type: $type`r`nContent-Length: $($body.Length)`r`nConnection: close`r`n`r`n"
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($head)
    $stream.Write($bytes, 0, $bytes.Length)
    if ($body.Length) { $stream.Write($body, 0, $body.Length) }
    $stream.Flush()
}

$deadline = [DateTime]::UtcNow.AddSeconds($Seconds)
try {
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not $listener.Pending()) { Start-Sleep -Milliseconds 20; continue }
        $client = $listener.AcceptTcpClient()
        try {
            $client.ReceiveTimeout = 10000
            $client.SendTimeout = 60000
            $stream = $client.GetStream()
            $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::ASCII, $false, 4096, $true)
            $requestLine = $reader.ReadLine()
            $userAgent = ''
            while ($line = $reader.ReadLine()) { if ($line -match '^User-Agent:\s*(.*)$') { $userAgent = $Matches[1] } } # headers, up to the blank line
            $parts = @(($requestLine ?? '') -split ' ') + @('', '')
            $method, $target = $parts[0], $parts[1]
            $status, $reason, $type, $body = 404, 'Not Found', 'text/plain', [System.Text.Encoding]::ASCII.GetBytes('not found')
            if ($method -ne 'GET') {
                $status, $reason, $body = 405, 'Method Not Allowed', [System.Text.Encoding]::ASCII.GetBytes('GET only')
            }
            elseif ($target.StartsWith('/')) {
                $path = [uri]::UnescapeDataString(($target -split '\?', 2)[0]).TrimStart('/') -replace '/', '\'
                $file = [System.IO.Path]::GetFullPath((Join-Path $Root $path))
                if ($file.StartsWith($Root + '\', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $file -PathType Leaf)) {
                    $status, $reason = 200, 'OK'
                    $type = if ($file.EndsWith('.msi')) { 'application/x-msdownload' } elseif ([System.IO.Path]::GetExtension($file) -eq '') { 'application/json; charset=utf-8' } else { 'application/octet-stream' }
                    $body = [System.IO.File]::ReadAllBytes($file)
                }
            }
            Send-Response $stream $status $reason $type $body
            [Console]::Out.WriteLine("$([DateTime]::Now.ToString('HH:mm:ss.fff')) $method $target -> $status ($($body.Length) bytes) [$userAgent]")
            [Console]::Out.Flush()
        }
        catch {
            [Console]::Out.WriteLine("$([DateTime]::Now.ToString('HH:mm:ss.fff')) error: $($_.Exception.Message)")
            [Console]::Out.Flush()
        }
        finally {
            $client.Dispose()
        }
    }
}
finally {
    $listener.Stop()
}
