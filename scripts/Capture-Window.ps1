param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$OutputPath, [string]$Title = 'Downlism')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (-not ('DownlismCapture' -as [type])) {
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DownlismCapture {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
 private delegate bool Callback(IntPtr h, IntPtr p);
 [DllImport("user32.dll")] private static extern bool EnumWindows(Callback c, IntPtr p);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint id);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder text, int size);
 [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
 public static IntPtr Find(int pid, string title) {
  IntPtr found=IntPtr.Zero;
  EnumWindows((h,p)=>{ GetWindowThreadProcessId(h,out uint id); var text=new StringBuilder(256); GetWindowText(h,text,256);
   if(id==pid && IsWindowVisible(h) && text.ToString()==title) {found=h; return false;} return true;
  },IntPtr.Zero); return found;
 }
}
'@
}
$previousDpi = [DownlismCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
$handle = [DownlismCapture]::Find($ProcessId, $Title)
if ($handle -eq [IntPtr]::Zero) { throw "No visible '$Title' window in process $ProcessId" }
$rect = New-Object DownlismCapture+RECT
[void][DownlismCapture]::GetWindowRect($handle, [ref]$rect)
$bitmap = [Drawing.Bitmap]::new($rect.Right-$rect.Left, $rect.Bottom-$rect.Top)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$dc = $graphics.GetHdc()
try {
 if (-not [DownlismCapture]::PrintWindow($handle, $dc, 2)) { throw 'PrintWindow failed' }
} finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
try {
 $full = [IO.Path]::GetFullPath($OutputPath)
 [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full)) | Out-Null
 $bitmap.Save($full, [Drawing.Imaging.ImageFormat]::Png)
 Write-Output $full
} finally { $bitmap.Dispose(); [void][DownlismCapture]::SetThreadDpiAwarenessContext($previousDpi) }
