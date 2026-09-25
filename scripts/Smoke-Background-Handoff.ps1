$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOWNLISM_MEMORY_TEST = '1'
$env:DOWNLISM_DATA_DIRECTORY = Join-Path $root 'artifacts/smoke-background-profile'
New-Item -ItemType Directory -Path $env:DOWNLISM_DATA_DIRECTORY -Force | Out-Null
$settings = Join-Path $env:DOWNLISM_DATA_DIRECTORY 'settings.json'
if (Test-Path -LiteralPath $settings) { Remove-Item -LiteralPath $settings }
$exe = Join-Path $root 'src/Downlism.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Downlism.App.exe'
if (!(Test-Path -LiteralPath $exe)) { throw 'Build Downlism.App in Debug x64 first.' }
$process = Start-Process -FilePath $exe -ArgumentList '--background' -PassThru -WindowStyle Hidden
try {
    Start-Sleep -Seconds 2
    $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', 'Downlism.Ingest.MemoryTest', [IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
        $json = '{"url":"https://example.com/sample.zip","fileName":"sample.zip"}'
        $bytes = [Text.Encoding]::UTF8.GetBytes($json)
        $prefix = [BitConverter]::GetBytes([int]$bytes.Length)
        $pipe.Write($prefix, 0, 4)
        $pipe.Write($bytes, 0, $bytes.Length)
        $pipe.Flush()
        $lengthBytes = [byte[]]::new(4)
        $read = $pipe.Read($lengthBytes, 0, 4)
        if ($read -ne 4) { throw 'No reply prefix.' }
        $length = [BitConverter]::ToInt32($lengthBytes, 0)
        $reply = [byte[]]::new($length)
        $offset = 0
        while ($offset -lt $length) {
            $received = $pipe.Read($reply, $offset, $length - $offset)
            if ($received -le 0) { break }
            $offset += $received
        }
        $response = [Text.Encoding]::UTF8.GetString($reply, 0, $offset) | ConvertFrom-Json
        if (!$response.accepted) { throw "Browser handoff was rejected: $($response.message)" }
    }
    finally { $pipe.Dispose() }
    Start-Sleep -Seconds 3
    $process.Refresh()
    if ($process.HasExited || $process.MainWindowTitle -ne '新增下載')
    { throw "The download prompt did not open after handoff: $($process.MainWindowTitle)" }
    [pscustomobject]@{
        Alive = -not $process.HasExited
        Window = $process.MainWindowTitle
        PrivateMB = if (!$process.HasExited) { [math]::Round($process.PrivateMemorySize64 / 1MB, 1) }
    } | Format-List
}
finally {
    $process.Refresh()
    if (!$process.HasExited) { Stop-Process -Id $process.Id }
    Remove-Item Env:DOWNLISM_MEMORY_TEST -ErrorAction SilentlyContinue
    Remove-Item Env:DOWNLISM_DATA_DIRECTORY -ErrorAction SilentlyContinue
}
