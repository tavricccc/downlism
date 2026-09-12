using System.Runtime.InteropServices;

namespace Downlism.App.Services;

/// <summary>
/// The tray presence. Downlism keeps running with its window closed, so this is where a
/// person checks on it, stops everything, or actually quits.
/// </summary>
/// <remarks>
/// It owns a hidden tool window rather than borrowing the main window: the shell dismisses a
/// tray menu only when its owner can be brought to the foreground, and the main window may be
/// hidden at the time. Adapted from the same control in Peeklism.
/// </remarks>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 21;
    private const uint IconId = 1;
    private const uint NotifyAdd = 0;
    private const uint NotifyModify = 1;
    private const uint NotifyDelete = 2;
    private const uint NotifySetVersion = 4;
    private const uint FlagMessage = 0x00000001;
    private const uint FlagIcon = 0x00000002;
    private const uint FlagTip = 0x00000004;
    private const uint FlagInfo = 0x00000010;
    private const uint InfoIconInfo = 0x00000001;
    private const uint IconVersion4 = 4;
    private const uint RightButtonUp = 0x0205;
    private const uint ContextMenu = 0x007B;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint NonClientSelect = 0x0400;
    private const uint MenuString = 0x0000;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuChecked = 0x0008;
    private const uint MenuDefault = 0x1000;
    private const uint TrackRightButton = 0x0002;
    private const uint TrackReturnCommand = 0x0100;
    private const uint CommandShow = 1;
    private const uint CommandPauseAll = 2;
    private const uint CommandLaunchAtLogin = 3;
    private const uint CommandExit = 4;
    private const string HostWindowClassName = "DownlismTrayHost";

    private readonly NativeMethods.WindowProcedure _windowProcedure;
    private readonly System.Drawing.Icon _icon;
    private readonly uint _taskbarCreatedMessage;
    private readonly nint _windowHandle;
    private NotifyIconData _iconData;
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        _icon = new System.Drawing.Icon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "Downlism.ico"), 32, 32);
        _windowProcedure = ProcessMessage;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _windowHandle = CreateHostWindow();
        AddIcon();
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? PauseAllRequested;

    public event EventHandler? LaunchAtLoginToggled;

    public event EventHandler? ExitRequested;

    /// <summary>Shown as a tick beside the startup item.</summary>
    public bool LaunchesAtLogin { get; set; }

    /// <summary>
    /// Summarises what is happening without opening the window, which is the whole reason to
    /// leave the app in the tray.
    /// </summary>
    public void UpdateTooltip(int activeDownloads, double bytesPerSecond)
    {
        if (!_added) return;

        _iconData.Tip = activeDownloads == 0
            ? "Downlism"
            : $"Downlism\n{activeDownloads} 個下載中，{ViewModels.DownloadRowViewModel.Bytes((long)bytesPerSecond)}/s";

        NativeMethods.Shell_NotifyIconW(NotifyModify, ref _iconData);
    }

    /// <summary>
    /// Shows a balloon. Used only for finishing and failing: a notification for anything that
    /// happens routinely is one people learn to dismiss without reading.
    /// </summary>
    public void Announce(string title, string message)
    {
        if (!_added) return;

        _iconData.Flags = FlagMessage | FlagIcon | FlagTip | FlagInfo;
        _iconData.InfoTitle = Trim(title, 63);
        _iconData.Info = Trim(message, 255);
        _iconData.InfoFlags = InfoIconInfo;
        NativeMethods.Shell_NotifyIconW(NotifyModify, ref _iconData);

        // Cleared afterwards so the next tooltip update does not raise the balloon again.
        _iconData.Flags = FlagMessage | FlagIcon | FlagTip;
        _iconData.InfoTitle = string.Empty;
        _iconData.Info = string.Empty;
    }

    private static string Trim(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            NativeMethods.Shell_NotifyIconW(NotifyDelete, ref _iconData);
            _added = false;
        }

        if (_windowHandle != 0) NativeMethods.DestroyWindow(_windowHandle);
        _icon.Dispose();
    }

    private nint CreateHostWindow()
    {
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            InstanceHandle = NativeMethods.GetModuleHandleW(null),
            ClassName = HostWindowClassName,
        };

        NativeMethods.RegisterClassExW(ref windowClass);
        return NativeMethods.CreateWindowExW(
            0x00000080, // WS_EX_TOOLWINDOW: never in the taskbar or Alt+Tab.
            HostWindowClassName,
            "Downlism",
            0, 0, 0, 0, 0, 0, 0,
            windowClass.InstanceHandle,
            0);
    }

    private void AddIcon()
    {
        _iconData = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _windowHandle,
            Id = IconId,
            Flags = FlagMessage | FlagIcon | FlagTip,
            CallbackMessage = CallbackMessage,
            IconHandle = _icon.Handle,
            Tip = "Downlism",
            Info = string.Empty,
            InfoTitle = string.Empty,
            VersionOrTimeout = IconVersion4,
        };

        _added = NativeMethods.Shell_NotifyIconW(NotifyAdd, ref _iconData);
        if (_added) NativeMethods.Shell_NotifyIconW(NotifySetVersion, ref _iconData);
    }

    private nint ProcessMessage(nint windowHandle, uint message, nuint wordParameter, nint longParameter)
    {
        // Explorer restarting drops every tray icon, and announces itself so they can be put back.
        if (message == _taskbarCreatedMessage)
        {
            _added = false;
            AddIcon();
            return 0;
        }

        if (message != CallbackMessage)
        {
            return NativeMethods.DefWindowProcW(windowHandle, message, wordParameter, longParameter);
        }

        var notification = unchecked((uint)(longParameter.ToInt64() & 0xFFFF));
        if (notification is LeftButtonDoubleClick)
        {
            ShowRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (notification is RightButtonUp or ContextMenu or NonClientSelect)
        {
            ShowMenu();
        }

        return 0;
    }

    private void ShowMenu()
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return;

        try
        {
            // Showing the window is the default item, so a double click does the obvious thing.
            NativeMethods.AppendMenuW(menu, MenuString | MenuDefault, CommandShow, "顯示 Downlism");
            NativeMethods.AppendMenuW(menu, MenuString, CommandPauseAll, "全部暫停");
            NativeMethods.AppendMenuW(menu, MenuSeparator, 0, null);
            NativeMethods.AppendMenuW(
                menu,
                MenuString | (LaunchesAtLogin ? MenuChecked : 0),
                CommandLaunchAtLogin,
                "登入 Windows 時啟動");
            NativeMethods.AppendMenuW(menu, MenuSeparator, 0, null);
            NativeMethods.AppendMenuW(menu, MenuString, CommandExit, "結束 Downlism");
            NativeMethods.GetCursorPos(out var point);

            // Without this the menu stays on screen after the user clicks elsewhere.
            NativeMethods.SetForegroundWindow(_windowHandle);
            var command = NativeMethods.TrackPopupMenuEx(
                menu, TrackRightButton | TrackReturnCommand, point.X, point.Y, _windowHandle, 0);

            switch (command)
            {
                case CommandShow:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandPauseAll:
                    PauseAllRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandLaunchAtLogin:
                    LaunchAtLoginToggled?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ExtraClassBytes;
        public int ExtraWindowBytes;
        public nint InstanceHandle;
        public nint IconHandle;
        public nint CursorHandle;
        public nint BackgroundBrush;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;

        public nint SmallIconHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint VersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private static class NativeMethods
    {
        internal delegate nint WindowProcedure(
            nint windowHandle, uint message, nuint wordParameter, nint longParameter);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern uint RegisterWindowMessageW(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern nint CreateWindowExW(
            uint extendedStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint DefWindowProcW(
            nint windowHandle, uint message, nuint wordParameter, nint longParameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint windowHandle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll")]
        internal static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AppendMenuW(nint menu, uint flags, nuint identifier, string? text);

        [DllImport("user32.dll")]
        internal static extern uint TrackPopupMenuEx(
            nint menu, uint flags, int x, int y, nint windowHandle, nint parameters);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(nint windowHandle);
    }
}
