using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Threading;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Error window with selectable, copyable text. The user of this program has no
/// debugger, so an exception must never disappear behind a window that closes in
/// a flash.
///
/// Anything may call <see cref="Show"/>, from any thread and at any point in the
/// life of the process - it is called from the exception handlers, after all. When
/// the UI is not up yet, or putting the window on the screen fails, the report
/// falls back to a plain Win32 message box.
/// </summary>
internal static class ErrorDialog
{
    public static void Show(string title, string details)
    {
        if (Application.Current is null)
        {
            // Too early (or too late) for a window: the toolkit is not running.
            NativeMethods.ShowMessageBox(title, details);
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ShowCore(title, details);
            return;
        }

        Dispatcher.UIThread.Post(() => ShowCore(title, details));
    }

    private static void ShowCore(string title, string details)
    {
        try
        {
            new ErrorWindow(title, details).Show();
        }
        catch (Exception ex)
        {
            Logger.Error("Could not show the error window.", ex);
            NativeMethods.ShowMessageBox(title, details);
        }
    }

    private sealed class ErrorWindow : AppWindow
    {
        private readonly TextBox _text;

        public ErrorWindow(string title, string details)
            : base(title)
        {
            HideOnClose = false;
            Width = 760;
            Height = 480;
            MinWidth = 480;
            MinHeight = 320;
            Topmost = true;

            _text = new TextBox
            {
                Text = details,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
                FontFamily = UiFonts.Monospace,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Stretch,
                [ScrollViewer.HorizontalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
                [ScrollViewer.VerticalScrollBarVisibilityProperty] = ScrollBarVisibility.Auto,
            };

            Button copy = new Button { Content = "复制到剪贴板", MinWidth = 130 };
            copy.Click += (_, _) => CopyToClipboard();

            Button close = new Button { Content = "关闭", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (_, _) => Close();

            StackPanel buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0),
            };
            buttons.Children.Add(copy);
            buttons.Children.Add(close);

            Grid root = new Grid
            {
                Margin = new Thickness(14),
                RowDefinitions = new RowDefinitions
                {
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto),
                },
            };
            Grid.SetRow(_text, 0);
            Grid.SetRow(buttons, 1);
            root.Children.Add(_text);
            root.Children.Add(buttons);

            Content = root;
        }

        private async void CopyToClipboard()
        {
            try
            {
                if (Clipboard is not null)
                {
                    await Clipboard.SetTextAsync(_text.Text ?? string.Empty);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not copy the error text to the clipboard: " + ex.Message);
            }
        }
    }
}
