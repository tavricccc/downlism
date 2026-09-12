# Exercises the browser handover path without a browser: speaks Chrome's native messaging
# framing to Downlism.Host.exe on stdin and reads its reply.
#
# Splits the question in two. If this passes, the app, the pipe and the host all work and any
# remaining fault is on the extension side; if it fails, the browser was never the problem.
param(
    [string]$InstallRoot = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Downlism' -ErrorAction SilentlyContinue).InstallLocation,
    [string]$Url = 'http://127.0.0.1:8899/sample.bin'
)
$ErrorActionPreference = 'Stop'

if (!$InstallRoot -or !(Test-Path $InstallRoot)) { throw 'Downlism is not installed.' }
$host_exe = Join-Path $InstallRoot 'Downlism.Host.exe'
if (!(Test-Path $host_exe)) { throw "Missing $host_exe" }

function Invoke-Host([string]$Json) {
    $info = [Diagnostics.ProcessStartInfo]::new($host_exe)
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true

    $process = [Diagnostics.Process]::Start($info)
    try {
        $payload = [Text.Encoding]::UTF8.GetBytes($Json)
        $stdin = $process.StandardInput.BaseStream
        $stdin.Write([BitConverter]::GetBytes([int]$payload.Length), 0, 4)
        $stdin.Write($payload, 0, $payload.Length)
        $stdin.Flush()

        $stdout = $process.StandardOutput.BaseStream
        $prefix = [byte[]]::new(4)
        $read = 0
        while ($read -lt 4) {
            $got = $stdout.Read($prefix, $read, 4 - $read)
            if ($got -le 0) { return $null }
            $read += $got
        }
        $length = [BitConverter]::ToInt32($prefix, 0)
        $buffer = [byte[]]::new($length)
        $read = 0
        while ($read -lt $length) {
            $got = $stdout.Read($buffer, $read, $length - $read)
            if ($got -le 0) { break }
            $read += $got
        }
        return [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
    } finally {
        $process.StandardInput.Close()
        if (!$process.WaitForExit(5000)) { $process.Kill() }
        $process.Dispose()
    }
}

Write-Output "Host: $host_exe"

Write-Output "`n1. ping（主程式未啟動時應回報未啟動或自動拉起）"
Invoke-Host '{"url":"","ping":true}'

Write-Output "`n2. 遞交一個下載"
$message = @{ url = $Url; fileName = 'sample.bin'; referrer = 'http://127.0.0.1/'; cookies = ''; userAgent = 'smoke'; totalBytes = 0 } | ConvertTo-Json -Compress
Invoke-Host $message

Start-Sleep -Seconds 3
Write-Output "`n3. 主程式是否在執行"
$app = Get-Process -Name Downlism.App -ErrorAction SilentlyContinue
if ($app) { "running: pid $($app.Id)" } else { 'not running' }
