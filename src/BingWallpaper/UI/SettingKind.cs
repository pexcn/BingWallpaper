namespace BingWallpaper.UI;

/// <summary>
/// Which setting the user just changed. The settings window persists every change
/// immediately and then says what it was, because the answers differ wildly: a new
/// market means a new download, a new fill mode only re-applies the picture that is
/// already on the desktop.
/// </summary>
internal enum SettingKind
{
    Market,
    Resolution,
    Fit,
    Theme,
    Interval,
    KeepDays,
    RunAtStartup,
}
