using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace BingWallpaper;

/// <summary>How a delete ended. Cancelled is not a failure: the user was asked
/// whether to delete a file the recycle bin could not take, and said no.</summary>
internal enum DeleteOutcome
{
    Deleted,
    Cancelled,
    Failed,
}

/// <summary>
/// P/Invoke declarations that are not theme related (theme interop lives in
/// Theme/DarkModeNative.cs). Every entry lists its Win32 name and the minimum
/// Windows version it requires.
/// </summary>
internal static class NativeMethods
{
    /// <summary>SPI_SETDESKWALLPAPER - SystemParametersInfoW action code.</summary>
    public const uint SPI_SETDESKWALLPAPER = 0x0014;

    /// <summary>SPIF_UPDATEINIFILE - persist the change to the user profile.</summary>
    public const uint SPIF_UPDATEINIFILE = 0x01;

    /// <summary>SPIF_SENDCHANGE - broadcast WM_SETTINGCHANGE so Explorer repaints.</summary>
    public const uint SPIF_SENDCHANGE = 0x02;

    /// <summary>WM_SETTINGCHANGE - broadcast when a system wide setting changes.</summary>
    public const int WM_SETTINGCHANGE = 0x001A;

    /// <summary>
    /// WS_EX_COMPOSITED - compose the window and its children off screen and put the
    /// result up in one piece, instead of revealing the window and letting the
    /// children paint themselves into it afterwards.
    /// </summary>
    public const int WS_EX_COMPOSITED = 0x02000000;

    /// <summary>FO_DELETE - SHFILEOPSTRUCTW.wFunc, shellapi.h.</summary>
    public const uint FO_DELETE = 0x0003;

    /// <summary>FOF_SILENT - no progress dialog for an operation this short.</summary>
    public const ushort FOF_SILENT = 0x0004;

    /// <summary>FOF_NOCONFIRMATION - do not ask before the delete itself.</summary>
    public const ushort FOF_NOCONFIRMATION = 0x0010;

    /// <summary>FOF_ALLOWUNDO - to the recycle bin rather than gone.</summary>
    public const ushort FOF_ALLOWUNDO = 0x0040;

    /// <summary>
    /// FOF_WANTNUKEWARNING - ask anyway when the recycle bin cannot take the file:
    /// it is turned off, too small, or the volume has none. Paired with
    /// FOF_NOCONFIRMATION on purpose - silent while the delete can be undone, a
    /// question when it cannot.
    /// </summary>
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>RECT, windef.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// PAINTSTRUCT, winuser.h. Only the device context is ever read back, but the
    /// fields before it have to be laid out for the window manager to write into -
    /// the reserved tail does not, so it gets no fields of its own. Size pins the
    /// struct to the 72 bytes the window manager writes on x64, which is what this
    /// executable targets (see PlatformTarget in the csproj).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    public struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public int Erase;
        public RECT PaintRectangle;
        public int Restore;
        public int IncUpdate;
    }

    /// <summary>
    /// SHFILEOPSTRUCTW, shellapi.h. The header packs this one to a single byte on 32
    /// bit builds only, so the natural layout is the right one here - this executable
    /// is x64, see PlatformTarget in the csproj.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct SHFILEOPSTRUCTW
    {
        public IntPtr Owner;
        public uint Function;

        /// <summary>A *list* of paths, terminated by an empty string - see RecycleFile.</summary>
        public string? From;

        public string? To;
        public ushort Flags;

        /// <summary>BOOL, written back by the shell. Set when the user answered a prompt with no.</summary>
        public int AnyOperationsAborted;

        public IntPtr NameMappings;
        public string? ProgressTitle;
    }

    /// <summary>
    /// user32!BeginPaint. Available since Windows 2000. Opens the painting cycle of
    /// a window: it hands back a device context and validates the update region, so
    /// it has to be paired with EndPaint or the window keeps asking to be painted.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    /// <summary>user32!EndPaint. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT paint);

    /// <summary>
    /// gdi32!SaveDC. Pushes the state of a device context - clipping region, selected
    /// objects, drawing modes - and returns a level to restore it by. Available since
    /// Windows 2000.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern int SaveDC(IntPtr hdc);

    /// <summary>gdi32!RestoreDC. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RestoreDC(IntPtr hdc, int savedState);

    /// <summary>CLR_INVALID - what GetPixel returns for a point it cannot read.</summary>
    public const uint CLR_INVALID = 0xFFFFFFFF;

    /// <summary>
    /// gdi32!GetPixel. Reads one pixel back out of a device context as a COLORREF -
    /// 0x00BBGGRR, so the bytes are the reverse of what Color.FromArgb takes.
    /// Available since Windows 2000.
    /// </summary>
    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>
    /// user32!SystemParametersInfoW. Available since Windows 2000.
    /// </summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, string pvParam, uint fWinIni);

    // ---- Desktop wallpaper crossfade, see WallpaperTransition.cs ----

    /// <summary>WM_PAINT - winuser.h.</summary>
    public const int WM_PAINT = 0x000F;

    /// <summary>WM_ERASEBKGND - winuser.h.</summary>
    public const int WM_ERASEBKGND = 0x0014;

    /// <summary>
    /// Undocumented Progman message; it has no name in any header, only the number.
    /// It asks Explorer to split the desktop into the window that hosts the icons and
    /// a WorkerW behind it - the same split Explorer makes for itself to crossfade a
    /// slideshow wallpaper. Once the split exists a window can be parented into the
    /// wallpaper layer, which is the only place a fade can be drawn without covering
    /// the icons. Known to work since Windows 7 and what every live wallpaper program
    /// relies on; Explorer ignores the message when the split is already there.
    /// </summary>
    public const uint WM_PROGMAN_SPAWN_WORKERW = 0x052C;

    /// <summary>SMTO_ABORTIFHUNG - do not sit out a hung Explorer, winuser.h.</summary>
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>WS_CHILD - winuser.h.</summary>
    public const int WS_CHILD = 0x40000000;

    /// <summary>WS_VISIBLE - winuser.h.</summary>
    public const int WS_VISIBLE = 0x10000000;

    /// <summary>WS_DISABLED - winuser.h. Belt to WS_EX_TRANSPARENT's braces.</summary>
    public const int WS_DISABLED = 0x08000000;

    /// <summary>
    /// WS_EX_LAYERED - winuser.h. On a *child* window only since Windows 8, which is
    /// below our minimum of build 19044.
    /// </summary>
    public const int WS_EX_LAYERED = 0x00080000;

    /// <summary>WS_EX_TRANSPARENT - hit testing falls through to what is underneath.</summary>
    public const int WS_EX_TRANSPARENT = 0x00000020;

    /// <summary>WS_EX_NOACTIVATE - never take the focus, winuser.h.</summary>
    public const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>LWA_ALPHA - SetLayeredWindowAttributes uses the alpha argument.</summary>
    public const uint LWA_ALPHA = 0x00000002;

    /// <summary>HWND_BOTTOM - SetWindowPos, bottom of the z order among siblings.</summary>
    public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

    /// <summary>SWP_NOSIZE - winuser.h.</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>SWP_NOMOVE - winuser.h.</summary>
    public const uint SWP_NOMOVE = 0x0002;

    /// <summary>SWP_NOACTIVATE - winuser.h.</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>SRCCOPY - BitBlt raster operation, wingdi.h.</summary>
    public const uint SRCCOPY = 0x00CC0020;

    /// <summary>
    /// DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, windef.h. A pseudo handle, not a
    /// pointer - the value itself is the contract.
    /// </summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    /// <summary>user32!EnumWindows callback. Returning false stops the enumeration.</summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr param);

    /// <summary>
    /// user32!EnumDisplayMonitors callback. The rectangle is the monitor's own, in
    /// virtual screen coordinates.
    /// </summary>
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr param);

    /// <summary>user32!FindWindowW. Available since Windows 2000.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? className, string? windowName);

    /// <summary>user32!FindWindowExW. Available since Windows 2000.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    /// <summary>user32!EnumWindows. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    /// <summary>user32!SendMessageTimeoutW. Available since Windows 2000.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint milliseconds, out IntPtr result);

    /// <summary>user32!GetWindowRect. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>user32!SetWindowPos. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>user32!UpdateWindow. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(IntPtr hWnd);

    /// <summary>
    /// user32!SetLayeredWindowAttributes. Available since Windows 2000, and since
    /// Windows 8 on child windows too.
    /// </summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    /// <summary>user32!EnumDisplayMonitors. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr param);

    /// <summary>
    /// user32!SetThreadDpiAwarenessContext. Available since Windows 10 version 1607
    /// (build 14393), which is below our minimum of build 19044. Returns the previous
    /// context, or NULL when the argument is not a valid one.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    /// <summary>user32!GetDC. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hWnd);

    /// <summary>user32!ReleaseDC. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    /// <summary>
    /// dwmapi!DwmFlush. Available since Windows Vista. Returns once the desktop
    /// compositor has put everything submitted so far on screen; the HRESULT is
    /// ignored because there is nothing to do about a compositor that is not there.
    /// </summary>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    public static extern int DwmFlush();

    /// <summary>gdi32!CreateCompatibleDC. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    /// <summary>gdi32!CreateCompatibleBitmap. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    /// <summary>gdi32!SelectObject. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

    /// <summary>gdi32!DeleteObject. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr handle);

    /// <summary>gdi32!DeleteDC. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);

    /// <summary>gdi32!BitBlt. Available since Windows 2000.</summary>
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    /// <summary>
    /// user32!GetDpiForSystem. Available since Windows 10 version 1607 (build 14393),
    /// which is below our minimum of build 19044.
    /// </summary>
    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    /// <summary>user32!SetForegroundWindow. Available since Windows 2000.</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>
    /// shell32!SHFileOperationW. Available since Windows 2000. Returns zero on
    /// success; everything else is one of shellapi.h's own DE_ codes rather than a
    /// Win32 error, which is why SetLastError is not set here.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHFileOperationW(ref SHFILEOPSTRUCTW operation);

    /// <summary>
    /// Sends one file to the recycle bin, the way Explorer's own delete does.
    ///
    /// <para>
    /// SHFileOperationW rather than IFileOperation, the COM interface that superseded
    /// it on Vista: this is one call on one file, and the newer way would cost an
    /// interface declaration, a CoCreateInstance and an apartment to run it in for
    /// exactly the same result. The old function still ships and still honours the
    /// recycle bin.
    /// </para>
    /// <para>
    /// The extra NUL is not a typo. pFrom is a list of paths ended by an empty
    /// string, so a single file has to be "path\0" - the marshaller appends the one
    /// that terminates the string itself.
    /// </para>
    /// </summary>
    public static DeleteOutcome RecycleFile(IntPtr owner, string path)
    {
        SHFILEOPSTRUCTW operation = new SHFILEOPSTRUCTW
        {
            Owner = owner,
            Function = FO_DELETE,
            From = path + "\0",
            Flags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT),
        };

        try
        {
            int result = SHFileOperationW(ref operation);
            if (result != 0)
            {
                Logger.Warn(
                    "shell: shfileoperation failed path=" + path
                    + " code=0x" + result.ToString("X", CultureInfo.InvariantCulture));
                return DeleteOutcome.Failed;
            }

            if (operation.AnyOperationsAborted != 0)
            {
                // The nuke warning was answered with no. Nothing was deleted, and the
                // shell has already said so on screen.
                Logger.Info("shell: delete cancelled path=" + path);
                return DeleteOutcome.Cancelled;
            }

            return DeleteOutcome.Deleted;
        }
        catch (Exception ex)
        {
            Logger.Error("shell: deleting a file failed path=" + path, ex);
            return DeleteOutcome.Failed;
        }
    }

    /// <summary>Reads the system DPI, falling back to 96 when the call fails.</summary>
    public static uint GetSystemDpiSafe()
    {
        try
        {
            uint dpi = GetDpiForSystem();
            return dpi == 0 ? 96u : dpi;
        }
        catch (Exception ex)
        {
            Logger.Warn("dpi: getdpiforsystem failed error=" + ex.Message);
            return 96u;
        }
    }
}
