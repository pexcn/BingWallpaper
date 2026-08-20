using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BingWallpaper.UI;

/// <summary>
/// The notification area icon, owned directly through Shell_NotifyIconW.
///
/// Avalonia has a TrayIcon of its own, but it can only ever open a Win32 popup
/// menu: it exposes no right click event, so there is no way to put a menu of our
/// own on the screen instead. Win32 menus are drawn by the system and stay light
/// on Windows 10 no matter what the application asks for, which is exactly the
/// thing this program refuses to live with. Owning the icon here costs one small
/// message window and gives back the right click, so the menu can be an ordinary
/// Avalonia window that follows the theme.
///
/// The window is a real top level window rather than a message-only one: message
/// only windows do not receive broadcasts, and "TaskbarCreated" - the message that
/// says Explorer restarted and every icon has to be added again - is a broadcast.
/// </summary>
internal sealed class TrayIconHost : IDisposable
{
    private const string WindowClassName = "BingWallpaper.TrayWindow";

    /// <summary>The callback message the shell sends for mouse input on the icon.</summary>
    private const int WM_TRAYICON = NativeMethods.WM_APP + 1;

    private const uint IconId = 1;

    /// <summary>
    /// The single live instance. The window procedure has to be a plain static
    /// function for Native AOT - it is called by Windows, so there is no delegate
    /// to carry state - and this program never owns more than one tray icon.
    /// </summary>
    private static TrayIconHost? _current;

    private static uint _taskbarCreatedMessage;

    private readonly IntPtr _hwnd;
    private string _toolTip = string.Empty;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconHost(string toolTip)
    {
        _current = this;
        _toolTip = toolTip;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _hwnd = CreateMessageWindow(RegisterWindowClass());
        AddIcon();
    }

    /// <summary>Left button double click, which opens the settings window.</summary>
    public event EventHandler? DoubleClicked;

    /// <summary>Right button release, with the screen position the menu belongs at.</summary>
    public event EventHandler<PixelPointEventArgs>? MenuRequested;

    /// <summary>Handle of the hidden window that owns the icon.</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>
    /// Sets the hover text. Windows reads at most 64 characters including the
    /// terminator, so the caller is expected to have shortened it already.
    /// </summary>
    public void SetToolTip(string text)
    {
        _toolTip = text ?? string.Empty;
        if (_iconAdded)
        {
            Send(NativeMethods.NIM_MODIFY, NativeMethods.NIF_TIP);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_iconAdded)
        {
            Send(NativeMethods.NIM_DELETE, 0);
            _iconAdded = false;
        }

        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
        }

        _current = null;
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero)
        {
            Logger.Error("The tray icon has no window to send its messages to.");
            return;
        }

        _iconAdded = Send(
            NativeMethods.NIM_ADD,
            NativeMethods.NIF_ICON | NativeMethods.NIF_MESSAGE | NativeMethods.NIF_TIP);

        if (!_iconAdded)
        {
            Logger.Error("Shell_NotifyIconW(NIM_ADD) failed, error " + Marshal.GetLastWin32Error() + ".");
        }
    }

    private unsafe bool Send(uint message, uint flags)
    {
        NativeMethods.NOTIFYICONDATAW data = default;
        data.cbSize = (uint)sizeof(NativeMethods.NOTIFYICONDATAW);
        data.hWnd = _hwnd;
        data.uID = IconId;
        data.uFlags = flags;
        data.uCallbackMessage = WM_TRAYICON;
        data.hIcon = AppIcon.Tray;

        // The buffer is 128 characters wide, but an icon registered with the
        // pre-Shell-5.0 behaviour - which is the one that still reports double
        // clicks - only has 64 of them read.
        string tip = _toolTip;
        int length = Math.Min(tip.Length, NativeMethods.TipLength - 1);
        for (int i = 0; i < length; i++)
        {
            data.szTip[i] = tip[i];
        }

        data.szTip[length] = '\0';

        return NativeMethods.Shell_NotifyIconW(message, in data);
    }

    private static unsafe ushort RegisterWindowClass()
    {
        fixed (char* className = WindowClassName)
        {
            NativeMethods.WNDCLASSEXW wndClass = default;
            wndClass.cbSize = (uint)sizeof(NativeMethods.WNDCLASSEXW);
            wndClass.lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, IntPtr, IntPtr>)&WndProc;
            wndClass.hInstance = NativeMethods.GetModuleHandleW(null);
            wndClass.lpszClassName = className;

            ushort atom = NativeMethods.RegisterClassExW(in wndClass);
            if (atom == 0)
            {
                Logger.Error("RegisterClassExW failed, error " + Marshal.GetLastWin32Error() + ".");
            }

            return atom;
        }
    }

    private static unsafe IntPtr CreateMessageWindow(ushort classAtom)
    {
        if (classAtom == 0)
        {
            return IntPtr.Zero;
        }

        fixed (char* className = WindowClassName)
        fixed (char* windowName = "BingWallpaper")
        {
            IntPtr hwnd = NativeMethods.CreateWindowExW(
                0,
                className,
                windowName,
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.GetModuleHandleW(null),
                IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                Logger.Error("CreateWindowExW failed, error " + Marshal.GetLastWin32Error() + ".");
            }

            return hwnd;
        }
    }

    /// <summary>
    /// Called by Windows, so it must be a static function with the platform calling
    /// convention and must never let an exception escape back into native code.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            TrayIconHost? host = _current;
            if (host is not null && !host._disposed)
            {
                if (msg == WM_TRAYICON)
                {
                    host.OnTrayMessage((int)lParam);
                    return IntPtr.Zero;
                }

                if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage)
                {
                    Logger.Info("Explorer restarted, adding the tray icon again.");
                    host._iconAdded = false;
                    host.AddIcon();
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("The tray window procedure threw.", ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>
    /// The icon was registered without NIM_SETVERSION, so lParam is the plain mouse
    /// message and the pointer position has to be asked for separately.
    /// </summary>
    private void OnTrayMessage(int mouseMessage)
    {
        switch (mouseMessage)
        {
            case NativeMethods.WM_LBUTTONDBLCLK:
                DoubleClicked?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_RBUTTONUP:
                MenuRequested?.Invoke(this, new PixelPointEventArgs(GetCursorPosition()));
                break;
        }
    }

    private static Avalonia.PixelPoint GetCursorPosition()
    {
        if (NativeMethods.GetCursorPos(out NativeMethods.POINT point))
        {
            return new Avalonia.PixelPoint(point.X, point.Y);
        }

        return default;
    }
}

/// <summary>A position in physical screen pixels.</summary>
internal sealed class PixelPointEventArgs : EventArgs
{
    public PixelPointEventArgs(Avalonia.PixelPoint position) => Position = position;

    public Avalonia.PixelPoint Position { get; }
}
