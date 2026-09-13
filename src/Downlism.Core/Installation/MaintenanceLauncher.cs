using System.Text;

namespace Downlism.Core.Installation;

public static class MaintenanceLauncher
{
    public static string CreateCommand(string target)
    {
        var uninstallExe = Path.Combine(target, "Uninstall.exe");
        if (File.Exists(uninstallExe))
        {
            return $"\"{uninstallExe}\"";
        }
        return $"\"{Path.Combine(target, "Downlism.Setup.exe")}\" --uninstall";
    }

    public static string CreateScript(string target, string localAppData) => """
        $ErrorActionPreference = 'Stop'
        $sourceRoot = __SOURCE__
        $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
        $maintenanceRoot = Join-Path $tempParent ('Downlism.Maintenance-' + [guid]::NewGuid().ToString('N'))
        function Assert-NoLinks([string]$path) {
            for ($ancestor = $path; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
                if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Maintenance path contains a reparse point.' }
            }
        }
        try {
            Assert-NoLinks $sourceRoot
            Assert-NoLinks $maintenanceRoot
            $manifest = Get-Content -LiteralPath (Join-Path $sourceRoot 'downlism-install.json') -Raw | ConvertFrom-Json
            if ($manifest.Product -ne 'Downlism') { throw 'Invalid installation record.' }
            $names = @($manifest.Files.PSObject.Properties.Name) + 'downlism-install.json'
            foreach ($name in $names) {
                if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':')) { throw 'Invalid maintenance path.' }
                $sourceFile = [IO.Path]::GetFullPath((Join-Path $sourceRoot $name))
                $copyFile = [IO.Path]::GetFullPath((Join-Path $maintenanceRoot $name))
                if (!$sourceFile.StartsWith($sourceRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or !$copyFile.StartsWith($maintenanceRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Maintenance path escapes its root.' }
                Assert-NoLinks $sourceFile
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copyFile)) | Out-Null
                Copy-Item -LiteralPath $sourceFile -Destination $copyFile
            }
            # The user must see and operate the uninstall window.
            $setup = Start-Process -FilePath (Join-Path $maintenanceRoot 'Downlism.Setup.exe') -ArgumentList '--uninstall' -PassThru -WindowStyle Normal
            $setup.WaitForExit()
            $marker = Join-Path $maintenanceRoot 'uninstall-complete'
            if (Test-Path -LiteralPath $marker) {
                $disposition = [IO.File]::ReadAllText($marker)
                if ($disposition -eq 'keep') { __KEEP__ }
                elseif ($disposition -eq 'remove') { __REMOVE__ }
                else { throw 'Invalid uninstall completion marker.' }
            }
        } catch {
            Add-Type -AssemblyName System.Windows.Forms
            [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Downlism') | Out-Null
        } finally {
            if ([IO.Path]::GetDirectoryName($maintenanceRoot) -ne $tempParent -or ![IO.Path]::GetFileName($maintenanceRoot).StartsWith('Downlism.Maintenance-')) { throw 'Invalid temporary cleanup path.' }
            Assert-NoLinks $maintenanceRoot
            if (Test-Path -LiteralPath $maintenanceRoot) {
                foreach ($entry in Get-ChildItem -LiteralPath $maintenanceRoot -Recurse -Force) { Assert-NoLinks $entry.FullName }
                Remove-Item -LiteralPath $maintenanceRoot -Recurse -Force
            }
        }
        """
        .Replace("__SOURCE__", "'" + Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)).Replace("'", "''", StringComparison.Ordinal) + "'", StringComparison.Ordinal)
        .Replace("__KEEP__", UninstallCleanup.CreateScript(localAppData, true, 0), StringComparison.Ordinal)
        .Replace("__REMOVE__", UninstallCleanup.CreateScript(localAppData, false, 0), StringComparison.Ordinal);
}
