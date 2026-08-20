using System;
using System.Drawing;

namespace BingWallpaper;

/// <summary>
/// One process wide scaling factor, captured from the system DPI at startup.
///
/// The application is system-DPI aware (see app.manifest), so a single factor is
/// correct for every window. Using this instead of Control.LogicalToDeviceUnits
/// keeps the owner drawn glyphs independent of the Windows Forms high DPI
/// internals, which on .NET Framework only activate through an app.config switch.
/// </summary>
internal static class DpiScale
{
    static DpiScale()
    {
        float scale = NativeMethods.GetSystemDpiSafe() / 96f;

        if (scale <= 0f || Math.Abs(scale - 1f) < 0.001f)
        {
            // GetDpiForSystem is unavailable or reported the default; ask GDI+ instead.
            try
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float fromGdi = graphics.DpiX / 96f;
                    if (fromGdi > 0f)
                    {
                        scale = fromGdi;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not read the desktop DPI: " + ex.Message);
            }
        }

        Scale = scale <= 0f ? 1f : scale;
    }

    /// <summary>1.0 at 96 DPI, 1.25 at 120 DPI, 1.4 at 134 DPI and so on.</summary>
    public static float Scale { get; }

    /// <summary>Converts a logical (96 DPI) pixel value to a device pixel value.</summary>
    public static int Round(int logicalPixels) => (int)Math.Round(logicalPixels * Scale, MidpointRounding.AwayFromZero);
}
