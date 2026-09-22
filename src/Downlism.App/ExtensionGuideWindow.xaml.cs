using System.Diagnostics;
using Downlism.App.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace Downlism.App;

/// <summary>
/// The four steps that load the browser extension, shown once after installing and available
/// from the command bar afterwards.
/// </summary>
/// <remarks>
/// The extension cannot install itself. Chrome only loads an unpacked extension through a
/// developer-mode folder picker, which is four steps in a place nobody visits, and the
/// installer has no way to drive it. Written instructions in a README would be read by nobody:
/// the moment they are needed is the moment installation finishes, which is exactly when this
/// opens.
///
/// Everything that can be done for the person is: the browser is opened at the right page, and
/// the one thing nobody can type from memory — the folder path — is a field with a copy button
/// beside it.
/// </remarks>
public sealed partial class ExtensionGuideWindow : Window
{
    private const double ContentWidth = 620;
    private readonly WindowDialogs _dialogs;

    /// <summary>Chromium reserves these pages, so a plain shell open is refused; the browser
    /// executable has to be launched with the URL as an argument instead.</summary>
    private const string ChromePage = "chrome://extensions";

    private const string EdgePage = "edge://extensions";

    public ExtensionGuideWindow()
    {
        InitializeComponent();
        _dialogs = new(this, Root);

        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon("Assets/Downlism.ico");

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        FolderPath.Text = ExtensionFolder;

        if (!Directory.Exists(ExtensionFolder))
        {
            // Only ever true when the app is run from a build output rather than an install.
            // Saying so beats a copy button that hands over a path to nothing.
            FolderHint.Text = "這個資料夾還不存在，代表目前執行的是未安裝的版本。請先執行安裝程式。";
            FolderHint.Visibility = Visibility.Visible;
        }

        // Sized from the measured content, like the per-download window: the folder path and
        // the hint below it are both of unpredictable length.
        Root.Loaded += (_, _) => FitToContent();
    }

    /// <summary>The extension ships inside the installation folder, beside the executable.</summary>
    public static string ExtensionFolder => Path.Combine(AppContext.BaseDirectory, "extension");

    private void FitToContent()
    {
        Root.Measure(new Windows.Foundation.Size(ContentWidth, double.PositiveInfinity));

        var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
        var height = Math.Max(Root.DesiredSize.Height, 240);

        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(
            (int)Math.Round(ContentWidth * scale),
            (int)Math.Ceiling(height * scale)));

        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;

        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + Math.Max(0, (work.Width - size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - size.Height) / 3)));

        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsResizable = false;
    }

    private void OpenChromeClick(object sender, RoutedEventArgs e) =>
        OpenBrowser(["chrome.exe", "google-chrome"], ChromePage, "Chrome");

    private void OpenEdgeClick(object sender, RoutedEventArgs e) =>
        OpenBrowser(["msedge.exe"], EdgePage, "Edge");

    /// <summary>
    /// Launches the browser at its extensions page, falling back to putting the address on the
    /// clipboard. The executable names are resolved through the shell's App Paths registry, so
    /// a browser installed anywhere is found without guessing at directories.
    /// </summary>
    private void OpenBrowser(string[] executables, string page, string name)
    {
        foreach (var executable in executables)
        {
            try
            {
                Process.Start(new ProcessStartInfo(executable)
                {
                    Arguments = page,
                    UseShellExecute = true,
                });

                Show($"已在 {name} 開啟擴充功能頁面。", InfoBarSeverity.Success);
                return;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // Not installed under this name; try the next one, then the clipboard.
            }
        }

        Copy(page);
        Show($"找不到 {name}。已複製 {page}，請自己貼到網址列。", InfoBarSeverity.Informational);
    }

    private void CopyPathClick(object sender, RoutedEventArgs e)
    {
        Copy(ExtensionFolder);
        Show("資料夾路徑已複製，可以貼到「載入未封裝項目」的位置欄。", InfoBarSeverity.Success);
    }

    private void OpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(ExtensionFolder))
        {
            Show("擴充功能資料夾不存在，請先執行安裝程式。", InfoBarSeverity.Error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ExtensionFolder}\"") { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Show("無法開啟檔案總管，請用上面的路徑自行前往。", InfoBarSeverity.Error);
        }
    }

    private static void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private void Show(string message, InfoBarSeverity severity)
        => _dialogs.ShowMessage(message, severity);
}
