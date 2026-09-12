# Serves one generated file over HTTP with byte-range support, for exercising the app against
# something that behaves like a real origin server without reaching the network.
#
# Built on TcpListener rather than HttpListener: the latter needs a URL ACL reservation, which
# means an elevated prompt on an ordinary developer machine. Connections are served on a
# runspace pool rather than one at a time, because a serial server would answer eight segment
# requests in sequence and make a parallel download look single-threaded.
param(
    [int]$Port = 8899,
    [int]$SizeMegabytes = 240,
    [int]$KilobytesPerSecond = 0
)
$ErrorActionPreference = 'Stop'

$size = $SizeMegabytes * 1MB
$content = [byte[]]::new($size)
for ($index = 0; $index -lt $size; $index += 997) { $content[$index] = [byte]($index % 251) }

$serve = {
    param($client, $content, $size, $KilobytesPerSecond)

    $stream = $client.GetStream()
    try {
        $buffer = [byte[]]::new(8192)
        $read = $stream.Read($buffer, 0, $buffer.Length)
        if ($read -le 0) { return }
        $request = [Text.Encoding]::ASCII.GetString($buffer, 0, $read)

        $start = 0
        $end = $size - 1
        $extra = ''
        $status = '200 OK'
        if ($request -match 'Range:\s*bytes=(\d+)-(\d*)') {
            $start = [long]$Matches[1]
            if ($Matches[2]) { $end = [long]$Matches[2] }
            if ($end -ge $size) { $end = $size - 1 }
            $status = '206 Partial Content'
            $extra = "Content-Range: bytes $start-$end/$size`r`n"
        }

        $length = $end - $start + 1
        $header = "HTTP/1.1 $status`r`nContent-Type: application/octet-stream`r`n" +
            "Accept-Ranges: bytes`r`nETag: `"sample-v1`"`r`nContent-Length: $length`r`n$extra" +
            "Content-Disposition: attachment; filename=`"sample.bin`"`r`nConnection: close`r`n`r`n"
        $headerBytes = [Text.Encoding]::ASCII.GetBytes($header)
        $stream.Write($headerBytes, 0, $headerBytes.Length)

        # Written in chunks so an optional throttle can make the transfer last long enough to
        # actually watch. The throttle is per connection, matching how a real server behaves.
        $chunk = 64KB
        $sent = 0
        while ($sent -lt $length) {
            $take = [Math]::Min($chunk, $length - $sent)
            $stream.Write($content, $start + $sent, $take)
            $sent += $take
            if ($KilobytesPerSecond -gt 0) {
                Start-Sleep -Milliseconds ([int](($take / 1KB) * 1000 / $KilobytesPerSecond))
            }
        }
        $stream.Flush()
    } catch {
        # A client that disappears mid-transfer is normal here.
    } finally {
        $stream.Dispose()
        $client.Dispose()
    }
}

$pool = [RunspaceFactory]::CreateRunspacePool(1, 24)
$pool.Open()
$running = [Collections.Generic.List[object]]::new()

$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
$listener.Start()
Write-Output "Serving $SizeMegabytes MB on http://127.0.0.1:$Port/sample.bin"

try {
    while ($true) {
        $client = $listener.AcceptTcpClient()

        $worker = [powershell]::Create()
        $worker.RunspacePool = $pool
        [void]$worker.AddScript($serve).AddArgument($client).AddArgument($content).
            AddArgument($size).AddArgument($KilobytesPerSecond)
        $running.Add(@{ Worker = $worker; Handle = $worker.BeginInvoke() })

        # Reap finished workers so a long-lived server does not accumulate handles.
        foreach ($entry in @($running)) {
            if ($entry.Handle.IsCompleted) {
                $entry.Worker.EndInvoke($entry.Handle)
                $entry.Worker.Dispose()
                [void]$running.Remove($entry)
            }
        }
    }
} finally {
    $listener.Stop()
    $pool.Close()
}
