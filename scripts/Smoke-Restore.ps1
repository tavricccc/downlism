# Restarts the app and checks that the previous session's downloads come back.
#
# The list lives in SQLite while per-segment progress lives in the sidecar beside the partial
# file, so this is the only way to confirm the two halves still agree after a restart.
param(
    [string]$Executable,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Executable) {
    $Executable = Join-Path $projectRoot 'src/Downlism.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Downlism.App.exe'
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/ui-smoke' }

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class RestoreWindow {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    public struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool Callback(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder title, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr handle, StringBuilder name, int size);
    // The tray host window carries the same title as the main window, so the class name is
    // what separates them. Matching on title alone returns whichever the shell enumerates
    // first, and the tray host has no UI beneath it.
    public static IntPtr Find(int processId) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((handle, parameter) => {
            GetWindowThreadProcessId(handle, out uint owner);
            if (owner == processId) {
                var title = new StringBuilder(256);
                GetWindowText(handle, title, title.Capacity);
                var cls = new StringBuilder(256);
                GetClassName(handle, cls, cls.Capacity);
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
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$process = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable) -PassThru
try {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw "App exited: $($process.ExitCode)" }
        $handle = [RestoreWindow]::Find($process.Id)
        if ($handle -ne [IntPtr]::Zero) {
            [void][RestoreWindow]::ShowWindow($handle, 5)
            [void][RestoreWindow]::SetForegroundWindow($handle)
        }
        # Pick the automation element belonging to that exact window. The process owns several
        # top-level windows, including the tray host, and FromHandle does not always return the
        # element whose subtree holds the XAML content.
        if ($handle -ne [IntPtr]::Zero) {
            $candidates = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
                [System.Windows.Automation.TreeScope]::Children, $condition)
            foreach ($candidate in $candidates) {
                if ([IntPtr]$candidate.Current.NativeWindowHandle -eq $handle) { $window = $candidate; break }
            }
        }
        if ($window) { break }
    }
    if (!$window) { throw 'No app window found.' }

    Start-Sleep -Seconds 2
    $handle = [RestoreWindow]::Find($process.Id)

    $rect = New-Object RestoreWindow+RECT
    [void][RestoreWindow]::GetWindowRect($handle, [ref]$rect)
    $bitmap = [Drawing.Bitmap]::new($rect.Right - $rect.Left, $rect.Bottom - $rect.Top)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    [void][RestoreWindow]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $path = Join-Path $OutputDirectory '5-restored.png'
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()

    # A restored row must be present and must be resumable, not merely listed.
    $resume = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, '繼續')
    $found = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $resume)
    if (!$found) { throw 'The restored download has no resume action.' }

    Write-Output $path
    Write-Output 'Restored a resumable download from the previous session.'
} finally {
    if (!$process.HasExited) { $process.Kill() }
}
