using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// One row of the tray menu: an optional check mark, a caption and - for the row
/// that shows the current picture - a second, smaller line above it.
///
/// It paints itself from <see cref="ThemeManager.Palette"/> rather than from the
/// Fluent theme resources, because the menu is one of the two surfaces this
/// program still draws itself and the palette is where those colours live.
/// </summary>
internal sealed class MenuRow : Border
{
    /// <summary>Height of an ordinary command row, in logical pixels.</summary>
    public const double CommandHeight = 32;

    /// <summary>Height of the two line row at the top of the menu.</summary>
    public const double TitleHeight = 48;

    private const double CheckColumnWidth = 26;

    private readonly TextBlock _check;
    private readonly TextBlock _caption;
    private readonly TextBlock _description;

    private readonly bool _twoLines;

    private bool _hovered;
    private bool _isChecked;

    public MenuRow(string caption, double height, bool twoLines = false)
    {
        _twoLines = twoLines;
        Height = height;
        Padding = new Thickness(6, 0, 12, 0);
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Arrow);

        _check = new TextBlock
        {
            Text = "✓",
            FontSize = 13,
            Width = CheckColumnWidth,
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _description = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11,
            IsVisible = false,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 1),
        };

        _caption = new TextBlock
        {
            Text = caption,
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel text = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(_description);
        text.Children.Add(_caption);

        Grid grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };

        Border checkCell = new Border { Width = CheckColumnWidth, Child = _check };
        Grid.SetColumn(checkCell, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(checkCell);
        grid.Children.Add(text);

        Child = grid;
        ApplyTheme();
    }

    /// <summary>Raised when the row was clicked while it was enabled.</summary>
    public event EventHandler? Invoked;

    public string Caption
    {
        get => _caption.Text ?? string.Empty;
        set => _caption.Text = value;
    }

    /// <summary>
    /// The small line above the caption; only the title row has one, and only while
    /// there is something to put in it - an empty line would push the caption
    /// off centre.
    /// </summary>
    public string Description
    {
        get => _description.Text ?? string.Empty;
        set
        {
            _description.Text = value;
            _description.IsVisible = _twoLines && !string.IsNullOrEmpty(value);
        }
    }

    /// <summary>Whether the check mark in front of the caption is shown.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            _check.IsVisible = value;
        }
    }

    /// <summary>Re-reads the palette, e.g. after the system switched to dark mode.</summary>
    public void ApplyTheme()
    {
        ThemePalette palette = ThemeManager.Palette;
        Background = _hovered && IsEnabled ? palette.Hover : Brushes.Transparent;
        _caption.Foreground = IsEnabled ? palette.Text : palette.DisabledText;
        _description.Foreground = IsEnabled ? palette.SecondaryText : palette.DisabledText;
        _check.Foreground = IsEnabled ? palette.Accent : palette.DisabledText;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // The colours follow the enabled state, exactly like the caption of a
        // greyed out menu item does everywhere else in Windows.
        if (change.Property == IsEnabledProperty)
        {
            _hovered = false;
            ApplyTheme();
        }
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hovered = true;
        ApplyTheme();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = false;
        ApplyTheme();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (!IsEnabled || e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        e.Handled = true;
        Invoked?.Invoke(this, EventArgs.Empty);
    }
}
