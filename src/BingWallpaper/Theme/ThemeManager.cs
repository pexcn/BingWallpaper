using System;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using Microsoft.Win32;

namespace BingWallpaper.Theme;

/// <summary>
/// One place decides whether the program is light or dark right now.
///
/// This used to be several hundred lines of owner drawing and undocumented uxtheme
/// ordinals, because Windows Forms has no dark mode below Windows 11. Avalonia
/// draws every pixel itself, so the whole thing collapses into "pick a theme
/// variant and tell the Fluent theme about it" - and it works the same on
/// Windows 10 as on Windows 11.
///
/// What is left to do by hand is following the system while the mode is
/// <see cref="ThemeMode.System"/>, and handing the two surfaces this program still
/// paints itself (the tray menu and the thumbnail tiles) a matching palette.
/// </summary>
internal static class ThemeManager
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ThemeMode _mode = ThemeMode.System;
    private static bool _subscribed;
    private static bool _applied;

    /// <summary>Raised after <see cref="Palette"/> changed.</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>Colours of the surfaces the program paints itself.</summary>
    public static ThemePalette Palette { get; private set; } = ThemePalette.Light;

    public static bool IsDark => Palette.IsDark;

    /// <summary>
    /// Called once the Avalonia application exists. Subscribing to the platform
    /// colour values is what makes a theme change in the Windows settings arrive
    /// while the program is running.
    /// </summary>
    public static void Initialize(ThemeMode mode)
    {
        _mode = mode;

        if (!_subscribed && Application.Current?.PlatformSettings is IPlatformSettings settings)
        {
            settings.ColorValuesChanged += OnColorValuesChanged;
            _subscribed = true;
        }

        Apply();
    }

    /// <summary>Switches to another mode, e.g. because the settings window changed it.</summary>
    public static void SetMode(ThemeMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        Apply();
    }

    /// <summary>
    /// Whether Windows is currently in dark mode. Answered by Avalonia once it is
    /// running, and by the registry value it reads itself before that - the startup
    /// log line is written before the UI exists.
    /// </summary>
    public static bool IsSystemDark()
    {
        try
        {
            if (Application.Current?.PlatformSettings is IPlatformSettings settings)
            {
                return settings.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the platform colour values: " + ex.Message);
        }

        return IsSystemDarkFromRegistry();
    }

    /// <summary>
    /// HKCU\...\Themes\Personalize\AppsUseLightTheme. Read only, and a missing value
    /// means light - that is what Windows itself assumes.
    /// </summary>
    private static bool IsSystemDarkFromRegistry()
    {
        try
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath, writable: false))
            {
                return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the system theme setting: " + ex.Message);
            return false;
        }
    }

    private static void OnColorValuesChanged(object? sender, PlatformColorValues values)
    {
        if (_mode != ThemeMode.System)
        {
            return;
        }

        Logger.Debug("The system colour scheme changed to " + values.ThemeVariant + ".");
        Apply();
    }

    private static void Apply()
    {
        bool dark = _mode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            _ => IsSystemDark(),
        };

        // Never ThemeVariant.Default: the effective variant is decided here, in one
        // place, so that the palette below and the stock controls cannot disagree
        // about which theme is in effect.
        if (Application.Current is Application app)
        {
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        }

        if (_applied && Palette.IsDark == dark)
        {
            return;
        }

        _applied = true;
        Palette = ThemePalette.For(dark);
        Logger.Info("Theme applied: mode=" + _mode + " effective=" + (dark ? "Dark" : "Light"));
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }
}
