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
