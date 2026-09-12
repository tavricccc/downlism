# Drives the real application against a local origin server and captures what it looks like.
#
# The point is to see the segment ribbon filling with genuine transfer data rather than to
# assert on a mock: if the ribbon is wrong, only a picture of a running download shows it.
param(
    [string]$Executable,
    [string]$OutputDirectory,
    [int]$Port = 8899,
    [int]$KilobytesPerSecond = 2500
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
public static class SmokeWindow {
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

function Save-Shot([IntPtr]$Handle, [string]$Name) {
    $rect = New-Object SmokeWindow+RECT
    [void][SmokeWindow]::GetWindowRect($Handle, [ref]$rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    # Flag 2 renders the full window content, including the composited Mica backdrop.
    [void][SmokeWindow]::PrintWindow($Handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)
    $path = Join-Path $OutputDirectory "$Name.png"
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
    Write-Output $path
}

function Invoke-Named([System.Windows.Automation.AutomationElement]$Window, [string]$Name) {
    # Match the button itself, not the TextBlock inside it: a name search alone finds the label
    # first, and a label has nothing to invoke.
    $condition = New-Object System.Windows.Automation.AndCondition(@(
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Button))))
    # The tree is still filling in for a moment after the window appears, so this waits rather
    # than failing on a race that has nothing to do with what is being tested.
    $element = $null
    for ($attempt = 0; $attempt -lt 40 -and !$element; $attempt++) {
        $element = $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (!$element) { Start-Sleep -Milliseconds 250 }
    }
    if (!$element) { throw "No button named $Name" }
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$server = Start-Process -FilePath 'pwsh' -PassThru -WindowStyle Hidden -ArgumentList @(
    '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Serve-TestFile.ps1'),
    '-Port', $Port, '-KilobytesPerSecond', $KilobytesPerSecond)
Start-Sleep -Seconds 3

$downloads = Join-Path $env:USERPROFILE 'Downloads'
Get-ChildItem -LiteralPath $downloads -Filter 'sample*' -ErrorAction SilentlyContinue | Remove-Item -Force

Set-Clipboard -Value "http://127.0.0.1:$Port/sample.bin"

$process = Start-Process -FilePath (Resolve-Path -LiteralPath $Executable) -PassThru
try {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)
    $window = $null
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw "App exited: $($process.ExitCode)" }
        $handle = [SmokeWindow]::Find($process.Id)
        if ($handle -ne [IntPtr]::Zero) {
            [void][SmokeWindow]::ShowWindow($handle, 5)
            [void][SmokeWindow]::SetForegroundWindow($handle)
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

    $handle = [SmokeWindow]::Find($process.Id)
    Start-Sleep -Seconds 1
    Save-Shot $handle '1-empty'

    Invoke-Named $window '貼上網址'
    Start-Sleep -Seconds 4
    Save-Shot $handle '2-downloading'

    Invoke-Named $window '暫停'
    Start-Sleep -Seconds 2
    Save-Shot $handle '3-paused'

    Invoke-Named $window '繼續'
    Start-Sleep -Seconds 20
    Save-Shot $handle '4-later'

    Get-ChildItem -LiteralPath $downloads -Filter 'sample*' -ErrorAction SilentlyContinue |
        Select-Object Name, Length | Format-Table | Out-String | Write-Output
} finally {
    if (!$process.HasExited) { $process.Kill() }
    if (!$server.HasExited) { $server.Kill() }
}
