using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Downlism.Core.Lifecycle;

namespace Downlism.Setup;

public partial class App : Application
{
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);
    private Window? _window;
    private SingleInstanceGate? _instanceGate;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var rawArgs = Environment.GetCommandLineArgs();
        var processName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        var isUninstall = rawArgs.Contains("--uninstall") || processName.Equals("Uninstall", StringComparison.OrdinalIgnoreCase);

        var target = InstallationService.InstalledPath;
        var currentDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

        // If running for uninstall directly from the installed target folder, replicate to %TEMP%
        // (following the bootstrap installer pattern) so the target directory can be deleted cleanly.
        if (isUninstall && target is not null &&
            currentDir.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var tempDir = Path.Combine(Path.GetTempPath(), "Downlism.Uninstall", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                foreach (var file in Directory.GetFiles(currentDir))
                {
                    File.Copy(file, Path.Combine(tempDir, Path.GetFileName(file)), true);
                }
                foreach (var dir in Directory.GetDirectories(currentDir))
                {
                    var dirName = Path.GetFileName(dir);
                    if (dirName.Equals("extension", StringComparison.OrdinalIgnoreCase)) continue;
                    var destSub = Path.Combine(tempDir, dirName);
                    Directory.CreateDirectory(destSub);
                    foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(dir, file);
                        var targetFile = Path.Combine(destSub, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
                        File.Copy(file, targetFile, true);
                    }
                }

                var psi = new ProcessStartInfo(Path.Combine(tempDir, "Downlism.Setup.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = tempDir,
                };
                psi.ArgumentList.Add("--uninstall");
                psi.ArgumentList.Add("--temp-root");
                psi.ArgumentList.Add(tempDir);
                foreach (var item in rawArgs.Skip(1))
                {
                    if (!item.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) &&
                        !item.StartsWith("--temp-root", StringComparison.OrdinalIgnoreCase))
                    {
                        psi.ArgumentList.Add(item);
                    }
                }
                Process.Start(psi);
                Exit();
                return;
            }
            catch
            {
                // If replication fails, fall through to normal launch
            }
        }

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _instanceGate = new SingleInstanceGate("Downlism.Setup", () => dispatcher.TryEnqueue(() =>
        {
            if (_window is null) return;
            ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 9);
            _window.Activate();
        }));
        if (!_instanceGate.IsPrimary) { _instanceGate.Dispose(); Exit(); return; }
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            _window = null;
            _instanceGate.Dispose();

            var tempRootIdx = Array.IndexOf(rawArgs, "--temp-root");
            if (tempRootIdx >= 0 && tempRootIdx + 1 < rawArgs.Length)
            {
                var tempPath = rawArgs[tempRootIdx + 1];
                if (Directory.Exists(tempPath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "cmd.exe",
                            Arguments = $"/c ping 127.0.0.1 -n 2 > nul & rmdir /s /q \"{tempPath}\"",
                            CreateNoWindow = true,
                            WindowStyle = ProcessWindowStyle.Hidden,
                        });
                    }
                    catch { }
                }
            }
        };
        _window.Activate();
        ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window), 5);
        _window.Activate();
    }
}
