using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace BingWallpaper.UI;

/// <summary>
/// The application icon, as Win32 icon handles.
///
/// The same .ico is both the Win32 icon of the executable (ApplicationIcon in the
/// project file, which is what Explorer and the task bar read) and an embedded
/// resource, which is what this class reads.
///
/// It hands out an HICON rather than an Avalonia <c>WindowIcon</c> on purpose. The
/// file carries a frame for 16, 20 and 24 pixels next to the big ones, and those
/// three were resampled from the 128 frame rather than stepped down - which is the
/// whole reason the tray icon looks sharp. Going through Avalonia would mean
/// handing it one bitmap and letting Skia scale it to whatever size the shell asks
/// for, so the prepared frames would go to waste. Picking the frame here and
/// letting Windows use it verbatim keeps them.
/// </summary>
internal static class AppIcon
{
    /// <summary>
    /// Assembly qualified name of the embedded icon. Set explicitly in the project
    /// file as well, so this string does not depend on any naming convention.
    /// </summary>
    private const string ResourceName = "BingWallpaper.Resources.app.ico";

    /// <summary>CreateIconFromResourceEx wants the resource format version of Windows 3.0.</summary>
    private const uint IconResourceVersion = 0x00030000;

    private static readonly byte[]? File = LoadFile();

    private static readonly Dictionary<int, IntPtr> Cache = new();

    /// <summary>Icon for the notification area, at the DPI of the current session.</summary>
    public static IntPtr Tray => GetHandle(
        NativeMethods.GetMetricForDpi(NativeMethods.SM_CXSMICON, NativeMethods.GetSystemDpiSafe(), 16));

    /// <summary>Icon for a title bar (the small frame).</summary>
    public static IntPtr WindowSmall => Tray;

    /// <summary>Icon for the task bar and Alt+Tab (the large frame).</summary>
    public static IntPtr WindowLarge => GetHandle(
        NativeMethods.GetMetricForDpi(NativeMethods.SM_CXICON, NativeMethods.GetSystemDpiSafe(), 32));

    /// <summary>
    /// Gives a window its icon. Avalonia leaves the window icon alone unless one is
    /// assigned, so without this the title bar shows the generic default.
    /// </summary>
    public static void ApplyToWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetIcon(hwnd, NativeMethods.ICON_SMALL, WindowSmall);
        SetIcon(hwnd, NativeMethods.ICON_BIG, WindowLarge);
    }

    private static void SetIcon(IntPtr hwnd, int which, IntPtr icon)
    {
        if (icon != IntPtr.Zero)
        {
            NativeMethods.SendMessageW(hwnd, NativeMethods.WM_SETICON, new IntPtr(which), icon);
        }
    }

    /// <summary>
    /// Creates - once per size - the icon handle for <paramref name="size"/> device
    /// pixels. The handles live for as long as the process does, which is exactly
    /// what the shell expects of an icon it has been handed.
    /// </summary>
    public static IntPtr GetHandle(int size)
    {
        if (size <= 0)
        {
            size = 16;
        }

        lock (Cache)
        {
            if (Cache.TryGetValue(size, out IntPtr cached))
            {
                return cached;
            }

            IntPtr handle = Create(size);
            Cache[size] = handle;
            return handle;
        }
    }

    private static unsafe IntPtr Create(int size)
    {
        byte[]? file = File;
        if (file is null)
        {
            return IntPtr.Zero;
        }

        try
        {
            if (!TryFindFrame(file, size, out int offset, out int length))
            {
                return IntPtr.Zero;
            }

            fixed (byte* bits = &file[offset])
            {
                IntPtr icon = NativeMethods.CreateIconFromResourceEx(
                    (IntPtr)bits,
                    (uint)length,
                    fIcon: true,
                    IconResourceVersion,
                    size,
                    size,
                    0);

                if (icon == IntPtr.Zero)
                {
                    Logger.Warn(
                        "CreateIconFromResourceEx failed for the " + size + "px frame, error " +
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ".");
                }

                return icon;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not build the " + size + "px application icon: " + ex.Message);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Picks the frame to hand to Windows: the exact size when the file has it, the
    /// next larger one otherwise (downscaling beats upscaling), and the largest one
    /// when every frame is smaller than what was asked for.
    /// <para>
    /// The layout being read here is the .ico file header: a three field ICONDIR
    /// followed by one 16 byte ICONDIRENTRY per frame.
    /// </para>
    /// </summary>
    private static bool TryFindFrame(byte[] file, int size, out int offset, out int length)
    {
        offset = 0;
        length = 0;

        if (file.Length < 6 || BitConverter.ToUInt16(file, 2) != 1)
        {
            Logger.Warn("The embedded application icon is not an .ico file.");
            return false;
        }

        int count = BitConverter.ToUInt16(file, 4);
        int best = -1;
        int bestWidth = 0;

        for (int i = 0; i < count; i++)
        {
            int entry = 6 + (i * 16);
            if (entry + 16 > file.Length)
            {
                break;
            }

            // A width of zero means 256 - the field is a single byte.
            int width = file[entry] == 0 ? 256 : file[entry];
            int frameLength = BitConverter.ToInt32(file, entry + 8);
            int frameOffset = BitConverter.ToInt32(file, entry + 12);
            if (frameLength <= 0 || frameOffset <= 0 || frameOffset + frameLength > file.Length)
            {
                continue;
            }

            if (best < 0 || IsBetter(width, bestWidth, size))
            {
                best = entry;
                bestWidth = width;
                offset = frameOffset;
                length = frameLength;
            }
        }

        if (best < 0)
        {
            Logger.Warn("The embedded application icon contains no usable frame.");
            return false;
        }

        return true;
    }

    private static bool IsBetter(int candidate, int current, int wanted)
    {
        if (candidate == wanted)
        {
            return true;
        }

        if (current == wanted)
        {
            return false;
        }

        bool candidateFits = candidate > wanted;
        bool currentFits = current > wanted;

        // Both larger than needed: take the smaller one. Both smaller: take the
        // larger one. One of each: prefer the one that has pixels to spare.
        if (candidateFits && currentFits)
        {
            return candidate < current;
        }

        if (!candidateFits && !currentFits)
        {
            return candidate > current;
        }

        return candidateFits;
    }

    private static byte[]? LoadFile()
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                Logger.Warn("The application icon is not embedded in the assembly: " + ResourceName);
                return null;
            }

            using MemoryStream buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the embedded application icon: " + ex.Message);
            return null;
        }
    }
}
