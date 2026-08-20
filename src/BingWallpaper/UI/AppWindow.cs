using System;
using Avalonia.Controls;
using Avalonia.Platform;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Base class of every ordinary window in this program. It carries the three
/// things all of them need and none of them should repeat: the interface font,
/// the multi frame application icon, and a title bar that follows the theme.
///
/// Closing hides the window instead of destroying it - there is no main window,
/// so a closed window has to stay reusable for the next time the tray menu asks
/// for it.
/// </summary>
internal abstract class AppWindow : Window
{
    private bool _reallyClosing;

    protected AppWindow(string title)
    {
        Title = title;
        FontFamily = UiFonts.Default;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeManager.ThemeChanged += OnThemeChanged;
    }

    /// <summary>
    /// Whether closing the window only hides it. True for the windows the tray menu
    /// opens again and again, false for a one-off like the error report.
    /// </summary>
    protected bool HideOnClose { get; init; } = true;

    /// <summary>Shows the window, or brings it to the front when it already is.</summary>
    public void ShowOrActivate()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        if (TryGetPlatformHandle() is IPlatformHandle handle && handle.Handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle.Handle);
        }
    }

    /// <summary>Closes the window for good, which is only ever done when the program exits.</summary>
    public void CloseForGood()
    {
        _reallyClosing = true;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ApplyNativeChrome();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Hide, do not destroy: the tray controller keeps the instance alive.
        if (HideOnClose && !_reallyClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        base.OnClosing(e);
    }

    /// <summary>Called when the palette changed; the base class repaints the chrome.</summary>
    protected virtual void OnThemeChanged()
    {
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ApplyNativeChrome();
        OnThemeChanged();
    }

    private void ApplyNativeChrome()
    {
        if (TryGetPlatformHandle() is not IPlatformHandle handle || handle.Handle == IntPtr.Zero)
        {
            return;
        }

        AppIcon.ApplyToWindow(handle.Handle);
        NativeMethods.SetDarkTitleBar(handle.Handle, ThemeManager.IsDark);
    }
}
