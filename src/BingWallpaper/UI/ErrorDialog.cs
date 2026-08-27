using System;
using System.Drawing;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Error dialog with selectable, copyable text. The user has no debugger, so an
/// exception must never disappear behind a window that closes in a flash.
/// </summary>
internal static class ErrorDialog
{
    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\n", "\r\n");

    public static void Show(string title, string details)
    {
        try
        {
            using Form form = new Form()
            {
                Text = title,
                Icon = AppIcon.Window,
                StartPosition = FormStartPosition.CenterScreen,
                Size = new Size(760, 480),
                MinimumSize = new Size(480, 320),
                ShowInTaskbar = true,
                AutoScaleDimensions = new SizeF(96F, 96F),
                AutoScaleMode = AutoScaleMode.Dpi,
                TopMost = true,
            };

            ThemeManager.ApplySystemFont(form);

            TextBox text = new TextBox()
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Text = NormalizeLineEndings(details),
                Font = new Font(FontFamily.GenericMonospace, 9f),
            };

            Panel buttons = new Panel()
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                Padding = new Padding(8),
            };

            ThemedButton copy = new ThemedButton()
            {
                Text = "复制到剪贴板",
                Width = 140,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            ThemedButton close = new ThemedButton()
            {
                Text = "关闭",
                Width = 100,
                Height = 30,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                DialogResult = DialogResult.OK,
            };

            buttons.Controls.Add(copy);
            buttons.Controls.Add(close);
            form.Controls.Add(text);
            form.Controls.Add(buttons);
            form.AcceptButton = close;
            form.CancelButton = close;

            void LayoutButtons()
            {
                close.Location = new Point(buttons.ClientSize.Width - close.Width - 8, 8);
                copy.Location = new Point(close.Left - copy.Width - 8, 8);
            }

            buttons.Resize += (_, _) => LayoutButtons();
            form.Shown += (_, _) => LayoutButtons();

            copy.Click += (_, _) =>
            {
                try
                {
                    Clipboard.SetText(text.Text);
                }
                catch (Exception ex)
                {
                    Logger.Warn("errordialog: copying to the clipboard failed error=" + ex.Message);
                }
            };

            form.HandleCreated += (_, _) => ThemeManager.ApplyToForm(form);
            ThemeManager.ApplyToForm(form);
            form.ShowDialog();
        }
        catch (Exception ex)
        {
            Logger.Error("errordialog: showing the dialog failed", ex);
            try
            {
                MessageBox.Show(details, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // Give up quietly - the log still has the original error.
            }
        }
    }
}
