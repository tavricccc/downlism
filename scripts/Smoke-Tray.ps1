# Checks the tray behaviour that has no visible surface to assert on any other way:
# closing the window must hide it rather than end the process, the window must come back, and
# --background must start without showing anything.
param([string]$Executable)
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Executable) {
    $Executable = Join-Path $projectRoot 'src/Downlism.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Downlism.App.exe'
}

Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TrayProbe {
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    private delegate bool Callback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder title, int size);
    // The main window, not the hidden tray host: both belong to the process, and only one of
    // them is the thing being shown and hidden.
    public static IntPtr FindMain(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((handle, parameter) => {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == processId) {
                var cls = new StringBuilder(256);
                GetClassName(handle, cls, cls.Capacity);
                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);
                if (title.ToString() == "Downlism" && cls.ToString() != "DownlismTrayHost") { found = handle; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

if (Get-Process -Name Downlism.App -ErrorAction SilentlyContinue) {
    throw 'Exit the running Downlism instance before this test.'
}

function Wait-Main([int]$ProcessId) {
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $handle = [TrayProbe]::FindMain($ProcessId)
        if ($handle -ne [IntPtr]::Zero) { return $handle }
    }
    throw 'No main window appeared.'
}

# 1. Normal launch shows the window; closing it hides the window but keeps the process alive.
$process = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable) -PassThru
try {
    $handle = Wait-Main $process.Id
    Start-Sleep -Seconds 1
    if (![TrayProbe]::IsWindowVisible($handle)) { throw 'The window did not appear on a normal launch.' }

    [void][TrayProbe]::PostMessage($handle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)  # WM_CLOSE
    Start-Sleep -Seconds 2

    if ($process.HasExited) { throw 'Closing the window ended the process instead of hiding it.' }
    if ([TrayProbe]::IsWindowVisible($handle)) { throw 'The window stayed visible after closing.' }
    Write-Output 'Closing the window hides it and leaves the app running.'

    # 2. It comes back, which is what the tray menu does.
    [void][TrayProbe]::ShowWindow($handle, 9)
    Start-Sleep -Milliseconds 750
    if (![TrayProbe]::IsWindowVisible($handle)) { throw 'The window did not come back.' }
    Write-Output 'The window can be brought back after hiding.'
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
}

Start-Sleep -Seconds 1

# 3. A sign-in launch takes the tray without putting a window on screen.
$background = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable) -PassThru -ArgumentList '--background'
try {
    $handle = Wait-Main $background.Id
    Start-Sleep -Seconds 2
    if ($background.HasExited) { throw 'The background launch exited.' }
    if ([TrayProbe]::IsWindowVisible($handle)) { throw '--background showed a window.' }
    Write-Output '--background starts without showing a window.'
} finally {
    if (!$background.HasExited) { $background.Kill(); $background.WaitForExit() }
}

Write-Output 'Tray behaviour verified.'
