using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace BingWallpaper.UI;

/// <summary>
/// The error window. It replaces the message box the Windows Forms version showed:
/// a full exception chain has to be readable, scrollable and copyable, which a
/// message box is none of.
///
/// Not a ContentDialog: a dialog needs an XamlRoot, and the failures worth showing
/// here happen when there is no window open at all.
/// </summary>
public sealed partial class ErrorWindow : Window
{
    private ErrorWindow(string title, string details)
    {
        InitializeComponent();
        WindowSupport.Prepare(this, title, resizable: true);
        WindowSupport.ResizeLogical(this, 760, 480);
        WindowSupport.Center(this);

        HeadlineText.Text = title;
        DetailsText.Text = NormalizeLineEndings(details);

        CopyButton.Click += (_, _) => CopyToClipboard();
        CloseButton.Click += (_, _) => Close();

        WindowSupport.ApplyTheme(this);
    }

    /// <summary>
    /// Shows a failure. Never throws: this is the last stop of the error path, and a
    /// second failure here would leave the user with nothing at all - so it falls
    /// back to a plain message box and, failing that, to the log alone.
    /// <para>
    /// <paramref name="closed"/> runs once the window is gone. The startup path uses
    /// it to end the process: a failure there leaves no tray icon behind, and the
    /// user has to be able to read the message before the program disappears.
    /// </para>
    /// </summary>
    internal static void Show(string title, string details, Action? closed = null)
    {
        try
        {
            ErrorWindow window = new ErrorWindow(title, details);
            if (closed is not null)
            {
                window.Closed += (_, _) => closed();
            }

            WindowSupport.ShowAndActivate(window);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not show the error window.", ex);
            NativeMethods.ShowError(title, details);
            closed?.Invoke();
        }
    }

    private void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Close();
    }

    private void CopyToClipboard()
    {
        try
        {
            DataPackage package = new DataPackage();
            package.SetText(DetailsText.Text);
            Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not copy the error text to the clipboard: " + ex.Message);
        }
    }

    private static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace("\n", "\r\n");
}
