using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Downlism.Bootstrap;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint window, string text, string caption, uint type);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // The outer apphost loads this assembly from resources; WinUI itself must
            // run from that directory so executable-relative XAML/PRI lookups still work.
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Downlism.Setup.exe"))
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("無法啟動安裝介面。");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception)
        {
            MessageBox(0, "無法開啟安裝程式。請保留 Setup 旁的 resources 資料夾。\n" + exception.Message, "Downlism", 0x10);
            return 1;
        }
    }
}
