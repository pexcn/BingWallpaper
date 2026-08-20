using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BingWallpaper.Theme;
using Microsoft.UI.Dispatching;

namespace BingWallpaper.UI;

/// <summary>Everything the tray menu can ask for.</summary>
internal enum TrayCommand
{
    /// <summary>The title row: opens the copyright link of the current picture.</summary>
    OpenSource = 1,

    Older,
    Newer,
    History,
    Refresh,
    Pin,
    Folder,
    Settings,
    Exit,
}

/// <summary>
/// What the menu shows the next time it is opened. The controller owns this and
/// hands over a new one whenever something changes; the tray only paints it.
/// </summary>
internal sealed class TrayMenuState
{
    public string Title { get; set; } = "正在获取今日壁纸…";

    /// <summary>Tool tip of the tray icon, at most 127 characters.</summary>
    public string Tooltip { get; set; } = "必应壁纸";

    /// <summary>Whether the title row has a link behind it and can be clicked.</summary>
    public bool SourceEnabled { get; set; }

    public bool OlderEnabled { get; set; }

    public bool NewerEnabled { get; set; }

    public bool Pinned { get; set; }

    public bool PinEnabled { get; set; }

    /// <summary>While a refresh or a switch runs, the commands that would collide are out.</summary>
    public bool Busy { get; set; }
}

/// <summary>
/// The notification area icon and its menu.
///
/// WinUI 3 has no tray icon and no menu that can be shown from outside a XAML
/// window, so this is Win32: a hidden top level window receives the callback
/// messages, and the menu is a real popup menu built with CreatePopupMenu.
///
/// The window is deliberately a normal (invisible, tool window) top level window
/// rather than a message-only one: message-only windows are excluded from
/// broadcasts, and WM_SETTINGCHANGE - which is how a theme switch is noticed - is
/// a broadcast.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const string WindowClassName = "BingWallpaper.TrayWindow";

    private const uint WM_APP = 0x8000;
    private const uint WM_TRAYCALLBACK = WM_APP + 1;
    private const uint WM_NULL = 0x0000;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    /// <summary>
    /// NOTIFYICON_VERSION (3), not _VERSION_4: version 3 keeps the classic callback
    /// format - the mouse message in lParam - while already allowing the 128
    /// character tool tip. Version 4 packs the anchor point into wParam instead and
    /// buys nothing here, because the menu is placed at the cursor anyway.
    /// </summary>
    private const uint NotifyIconVersion = 3;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_SEPARATOR = 0x00000800;

    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_NONOTIFY = 0x0080;
    private const uint TPM_RETURNCMD = 0x0100;

    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_OVERLAPPED = 0x00000000;

    /// <summary>The one and only tray entry of this process.</summary>
    private const uint IconId = 1;

    /// <summary>
    /// Kept alive for the lifetime of the process: Windows holds a raw function
    /// pointer to it, and a collected delegate is a crash the moment a message
    /// arrives.
    /// </summary>
    private static readonly WndProcDelegate Procedure = WindowProc;

    private static readonly Dictionary<IntPtr, TrayIcon> Instances = new Dictionary<IntPtr, TrayIcon>();

    private static ushort _windowClass;

    private static uint _taskbarCreatedMessage;

    private readonly Action<TrayCommand> _command;
    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _window;

    private TrayMenuState _state = new TrayMenuState();
    private IntPtr _icon;
    private bool _added;
    private bool _disposed;

    public TrayIcon(Action<TrayCommand> command)
    {
        _command = command;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _window = CreateHiddenWindow();
        Instances[_window] = this;
        _icon = AppIcon.LoadTrayIcon(GetWindowDpi());
        Add();
    }

    /// <summary>Raised when Windows broadcasts that the colour scheme changed.</summary>
    public event EventHandler? SystemColorSchemeChanged;

    /// <summary>The hidden window, which the menu needs as its owner.</summary>
    public IntPtr Handle => _window;

    /// <summary>Replaces what the menu will show and refreshes the tool tip.</summary>
    public void Update(TrayMenuState state)
    {
        _state = state;
        if (_added)
        {
            Notify(NIM_MODIFY, NIF_TIP, state.Tooltip);
        }
    }

    /// <summary>
    /// Lets the menu follow the current theme. The palette itself is process wide
    /// (see <see cref="DarkModeNative.SetAppMode"/>); this is the per window half of it.
    /// </summary>
    public void ApplyTheme(bool dark) => DarkModeNative.AllowDarkModeForHandle(_window, dark);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_added)
        {
            Notify(NIM_DELETE, 0, string.Empty);
            _added = false;
        }

        Instances.Remove(_window);
        AppIcon.Destroy(_icon);
        _icon = IntPtr.Zero;

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
        }
    }

    private static IntPtr CreateHiddenWindow()
    {
        IntPtr instance = GetModuleHandleW(null);
        if (_windowClass == 0)
        {
            WNDCLASSEXW windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Procedure),
                hInstance = instance,
                lpszClassName = WindowClassName,
            };

            _windowClass = RegisterClassExW(ref windowClass);
            if (_windowClass == 0)
            {
                throw new InvalidOperationException(
                    "RegisterClassEx failed for the tray window: " + Marshal.GetLastWin32Error());
            }
        }

        if (_taskbarCreatedMessage == 0)
        {
            // Explorer broadcasts this after a restart; the icon has to be added again.
            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        }

        IntPtr window = CreateWindowExW(
            WS_EX_TOOLWINDOW,
            WindowClassName,
            "必应壁纸",
            WS_OVERLAPPED,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);

        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "CreateWindowEx failed for the tray window: " + Marshal.GetLastWin32Error());
        }

        return window;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (!Instances.TryGetValue(hwnd, out TrayIcon? instance))
        {
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        try
        {
            return instance.HandleMessage(hwnd, message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // A managed exception must never travel through the Win32 message
            // dispatcher; it would tear the process down without a log entry.
            Logger.Error("Tray window procedure failed for message 0x" + message.ToString("X4") + ".", ex);
            return DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_TRAYCALLBACK)
        {
            uint mouseMessage = (uint)(lParam.ToInt64() & 0xFFFF);
            if (mouseMessage == WM_RBUTTONUP || mouseMessage == WM_CONTEXTMENU)
            {
                ShowMenu();
            }
            else if (mouseMessage == WM_LBUTTONDBLCLK)
            {
                Dispatch(TrayCommand.Settings);
            }

            return IntPtr.Zero;
        }

        if (message == WM_COMMAND)
        {
            Dispatch((TrayCommand)(wParam.ToInt64() & 0xFFFF));
            return IntPtr.Zero;
        }

        if (message == WM_SETTINGCHANGE)
        {
            string? area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
            if (string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal))
            {
                Logger.Debug("WM_SETTINGCHANGE/ImmersiveColorSet received.");
                SystemColorSchemeChanged?.Invoke(this, EventArgs.Empty);
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        if (message == WM_DPICHANGED)
        {
            RefreshIcon();
            return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            Logger.Info("Explorer restarted, adding the tray icon again.");
            _added = false;
            RefreshIcon();
            Add();
            return IntPtr.Zero;
        }

        if (message == WM_DESTROY)
        {
            Instances.Remove(hwnd);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private void Add()
    {
        if (_added || _disposed)
        {
            return;
        }

        if (!Notify(NIM_ADD, NIF_MESSAGE | NIF_ICON | NIF_TIP, _state.Tooltip))
        {
            Logger.Error("Shell_NotifyIcon(NIM_ADD) failed: " + Marshal.GetLastWin32Error());
            return;
        }

        _added = true;

        // Asks the shell for the modern behaviour; a failure here costs the longer
        // tool tip and nothing else, so it is logged rather than thrown.
        NOTIFYICONDATAW data = CreateData(0, string.Empty);
        data.uTimeoutOrVersion = NotifyIconVersion;
        if (!Shell_NotifyIconW(NIM_SETVERSION, ref data))
        {
            Logger.Debug("Shell_NotifyIcon(NIM_SETVERSION) failed: " + Marshal.GetLastWin32Error());
        }
    }

    /// <summary>Reloads the icon at the current DPI and hands it to the shell.</summary>
    private void RefreshIcon()
    {
        IntPtr previous = _icon;
        _icon = AppIcon.LoadTrayIcon(GetWindowDpi());
        if (_added)
        {
            Notify(NIM_MODIFY, NIF_ICON, string.Empty);
        }

        AppIcon.Destroy(previous);
    }

    private uint GetWindowDpi()
    {
        try
        {
            uint dpi = _window != IntPtr.Zero ? NativeMethods.GetDpiForWindow(_window) : 0;
            return dpi == 0 ? NativeMethods.GetSystemDpiSafe() : dpi;
        }
        catch (Exception ex)
        {
            Logger.Debug("GetDpiForWindow failed: " + ex.Message);
            return 96u;
        }
    }

    private bool Notify(uint action, uint flags, string tooltip)
    {
        NOTIFYICONDATAW data = CreateData(flags, tooltip);
        return Shell_NotifyIconW(action, ref data);
    }

    private NOTIFYICONDATAW CreateData(uint flags, string tooltip) => new NOTIFYICONDATAW
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAYCALLBACK,
        hIcon = _icon,
        szTip = Truncate(tooltip, 127),
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void ShowMenu()
    {
        TrayMenuState state = _state;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            Logger.Error("CreatePopupMenu failed: " + Marshal.GetLastWin32Error());
            return;
        }

        try
        {
            // The title row doubles as the "open the image source" command; without a
            // link behind it, it is greyed out and reads as a plain header.
            AppendItem(menu, Truncate(state.Title, 80), TrayCommand.OpenSource, state.SourceEnabled && !state.Busy);
            AppendSeparator(menu);
            AppendItem(menu, "上一张", TrayCommand.Older, state.OlderEnabled && !state.Busy);
            AppendItem(menu, "下一张", TrayCommand.Newer, state.NewerEnabled && !state.Busy);
            AppendItem(menu, "选择日期…", TrayCommand.History, !state.Busy);
            AppendItem(menu, "立即刷新", TrayCommand.Refresh, !state.Busy);
            AppendItem(menu, "固定当前壁纸", TrayCommand.Pin, state.PinEnabled && !state.Busy, state.Pinned);
            AppendSeparator(menu);
            AppendItem(menu, "打开壁纸目录", TrayCommand.Folder, true);
            AppendSeparator(menu);
            AppendItem(menu, "设置…", TrayCommand.Settings, true);
            AppendItem(menu, "退出", TrayCommand.Exit, true);

            if (!GetCursorPos(out POINT point))
            {
                Logger.Warn("GetCursorPos failed, showing the menu at the top left corner.");
                point = default;
            }

            // Without this the menu refuses to close when the user clicks elsewhere;
            // the trailing WM_NULL is the second half of that documented dance.
            NativeMethods.SetForegroundWindow(_window);

            uint selected = TrackPopupMenuEx(
                menu,
                TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                point.X,
                point.Y,
                _window,
                IntPtr.Zero);

            PostMessageW(_window, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (selected != 0)
            {
                Dispatch((TrayCommand)selected);
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private static void AppendItem(IntPtr menu, string text, TrayCommand command, bool enabled, bool @checked = false)
    {
        uint flags = MF_STRING | (enabled ? 0u : MF_GRAYED) | (@checked ? MF_CHECKED : 0u);
        if (!AppendMenuW(menu, flags, (UIntPtr)(uint)command, text))
        {
            Logger.Warn("AppendMenu failed for \"" + text + "\": " + Marshal.GetLastWin32Error());
        }
    }

    private static void AppendSeparator(IntPtr menu) => AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);

    /// <summary>
    /// Runs a command outside the menu's modal loop. TrackPopupMenuEx is still on
    /// the stack when the selection comes back, and opening a window from in there
    /// leaves both the menu and the window in a bad state.
    /// </summary>
    private void Dispatch(TrayCommand command)
    {
        if (!Enum.IsDefined(typeof(TrayCommand), command))
        {
            return;
        }

        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    _command(command);
                }
                catch (Exception ex)
                {
                    Logger.Error("Tray command " + command + " failed.", ex);
                }
            }))
        {
            Logger.Warn("Could not enqueue the tray command " + command + ".");
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value.Substring(0, maxLength - 1) + "…";

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessageW(string message);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? item);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
