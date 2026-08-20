using Avalonia.Media;

namespace BingWallpaper.UI;

/// <summary>
/// The font every window of this program uses.
///
/// Avalonia does not take the font from the system, it takes it from the theme,
/// and the Fluent theme asks for Segoe UI. Segoe UI has no Chinese glyphs, and
/// this program's entire interface is Chinese, so it is named explicitly here.
/// Microsoft YaHei UI is the face Windows itself uses for a Chinese interface;
/// Segoe UI stays in the list as the fallback for an installation that somehow
/// does not have it.
/// </summary>
internal static class UiFonts
{
    public static FontFamily Default { get; } = new FontFamily("Microsoft YaHei UI, Segoe UI");

    /// <summary>Used for the stack traces in the error window.</summary>
    public static FontFamily Monospace { get; } = new FontFamily("Consolas, Courier New");
}
