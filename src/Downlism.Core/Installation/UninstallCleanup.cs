namespace Downlism.Core.Installation;

/// <summary>Runs outside the cached installer so its last executable can be removed.</summary>
public static class UninstallCleanup
{
    public static string CreateScript(string localAppData, bool keepData, int bootstrapProcessId)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        var root = Path.Combine(parent, "Downlism");
        var target = keepData ? Path.Combine(root, "Installer") : root;
        return """
            $ErrorActionPreference = 'Stop'
            $cleanupRoot = __ROOT__
            $cleanupTarget = __TARGET__
            $cleanupParent = __PARENT__
            try {
                if (__PID__ -gt 0) { Wait-Process -Id __PID__ -Timeout 120 -ErrorAction SilentlyContinue }
                if (__PID__ -gt 0 -and (Get-Process -Id __PID__ -ErrorAction SilentlyContinue)) { throw 'Installer is still running.' }
                if ([IO.Path]::GetFullPath($cleanupRoot) -ne (Join-Path $cleanupParent 'Downlism')) { throw 'Invalid cleanup root.' }
                if ($cleanupTarget -ne $cleanupRoot -and $cleanupTarget -ne (Join-Path $cleanupRoot 'Installer')) { throw 'Invalid cleanup target.' }
                for ($ancestor = $cleanupTarget; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
                    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Cleanup path contains a reparse point.' }
                }
                if (Test-Path -LiteralPath $cleanupTarget) {
                    $links = @(Get-ChildItem -LiteralPath $cleanupTarget -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
                    if ($links.Count -gt 0) { throw 'Cleanup contents contain a reparse point.' }
                    Remove-Item -LiteralPath $cleanupTarget -Recurse -Force
                }
                if ((Test-Path -LiteralPath $cleanupRoot) -and !(Get-ChildItem -LiteralPath $cleanupRoot -Force | Select-Object -First 1)) {
                    Remove-Item -LiteralPath $cleanupRoot
                }
            } catch {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.MessageBox]::Show("Downlism could not remove: $cleanupTarget`n$($_.Exception.Message)", 'Downlism cleanup') | Out-Null
                exit 1
            }
            """
            .Replace("__ROOT__", Quote(root), StringComparison.Ordinal)
            .Replace("__TARGET__", Quote(target), StringComparison.Ordinal)
            .Replace("__PARENT__", Quote(parent), StringComparison.Ordinal)
            .Replace("__PID__", bootstrapProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static string Quote(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
