using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using BingWallpaper.Theme;
using BingWallpaper.UI;

namespace BingWallpaper;

/// <summary>
/// The Avalonia application object.
///
/// It is built in C# rather than in XAML - the same choice the Windows Forms
/// version made, and for the same reason: there is no designer in this project, so
/// a markup file would only add a second place to look. Compiled XAML would work
/// under Native AOT just as well; this is a matter of taste, not of capability.
/// </summary>
internal sealed class App : Application
{
    /// <summary>
    /// The live controller, or null before the UI is up and after it is gone. The
    /// single instance listener runs on a thread of its own and needs a way in.
    /// </summary>
    public static TrayController? Controller { get; private set; }

    /// <summary>
    /// The configuration, read before the UI was created. Static because Avalonia
    /// constructs this class itself and offers no place to hand anything in.
    /// </summary>
    public static AppConfig? Configuration { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        base.Initialize();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        AppConfig config = Configuration ?? new AppConfig();

        // Before anything is on the screen: the first window has to open in the
        // right theme, not switch to it a moment later.
        ThemeManager.Initialize(config.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Controller = new TrayController(config);
            desktop.Exit += OnExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // The tray icon has to be taken out of the notification area explicitly;
        // an icon whose process is gone stays as a ghost until the user hovers it.
        Controller?.Dispose();
        Controller = null;
        Logger.Info("Message loop finished, exiting.");
    }
}
