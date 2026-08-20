using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// One entry of the history grid: a thumbnail with the date and the title under it.
///
/// Everything is drawn from the palette rather than from the theme resources, so
/// the frame, the hover state and the "current"/"pinned" badge look the same in
/// both themes and stay in step with the tray menu.
/// </summary>
internal sealed class ThumbnailTile : Border
{
    /// <summary>Logical pixels. The picture is 16:9, the rest holds the two lines.</summary>
    public const double TileWidth = 200;

    public const double TileHeight = 158;

    public const double TileMargin = 8;

    private static readonly double PictureHeight = Math.Round(TileWidth * 9 / 16);

    private readonly Border _pictureFrame;
    private readonly Image _picture = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _placeholder;
    private readonly Border _badge;
    private readonly TextBlock _badgeText;
    private readonly TextBlock _date;
    private readonly TextBlock _title;

    private bool _hovered;
    private bool _isCurrent;
    private bool _isPinned;

    public ThumbnailTile(int index, BingImageInfo info)
    {
        Index = index;
        Info = info;

        Width = TileWidth;
        Height = TileHeight;
        Margin = new Thickness(TileMargin);
        Background = Brushes.Transparent;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);

        _placeholder = new TextBlock
        {
            Text = "载入中…",
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _badgeText = new TextBlock
        {
            Text = "当前",
            FontSize = 11,
            Margin = new Thickness(6, 3, 6, 3),
        };

        _badge = new Border
        {
            Child = _badgeText,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            IsVisible = false,
        };

        Grid pictureArea = new Grid();
        pictureArea.Children.Add(_placeholder);
        pictureArea.Children.Add(_picture);
        pictureArea.Children.Add(_badge);

        _pictureFrame = new Border
        {
            Height = PictureHeight,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = pictureArea,
        };

        _date = new TextBlock
        {
            Text = info.DisplayDate,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        _title = new TextBlock
        {
            Text = info.DisplayTitle,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        StackPanel content = new StackPanel { Orientation = Orientation.Vertical };
        content.Children.Add(_pictureFrame);
        content.Children.Add(_date);
        content.Children.Add(_title);

        Child = content;
        ApplyTheme();
    }

    /// <summary>Raised when the tile was clicked or activated from the keyboard.</summary>
    public event EventHandler? Invoked;

    /// <summary>Position in the metadata list - the index the tray controller applies by.</summary>
    public int Index { get; }

    public BingImageInfo Info { get; }

    /// <summary>The tile owns the bitmap and disposes the one it replaces.</summary>
    public Bitmap? Thumbnail
    {
        get => _picture.Source as Bitmap;
        set
        {
            Bitmap? previous = _picture.Source as Bitmap;
            if (ReferenceEquals(previous, value))
            {
                return;
            }

            _picture.Source = value;
            _placeholder.IsVisible = value is null;
            previous?.Dispose();
        }
    }

    /// <summary>Whether this entry is the wallpaper currently on the desktop.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value)
            {
                return;
            }

            _isCurrent = value;
            ApplyTheme();
        }
    }

    /// <summary>
    /// Whether this entry is the pinned wallpaper. Only ever set on the tile that is
    /// current as well - the pin always follows the wallpaper on the desktop - so it
    /// relabels the badge that is already there instead of adding a second one.
    /// </summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }

            _isPinned = value;
            ApplyTheme();
        }
    }

    /// <summary>Re-reads the palette, e.g. after the system switched to dark mode.</summary>
    public void ApplyTheme()
    {
        ThemePalette palette = ThemeManager.Palette;

        _pictureFrame.Background = palette.ControlBackground;
        _pictureFrame.BorderBrush = _isCurrent || _hovered ? palette.Accent : palette.Border;
        _pictureFrame.BorderThickness = new Thickness(_isCurrent ? 2 : 1);
        _placeholder.Foreground = palette.SecondaryText;
        _date.Foreground = palette.SecondaryText;
        _title.Foreground = palette.Text;

        _badge.IsVisible = _isCurrent;
        _badge.Background = palette.Accent;
        _badgeText.Foreground = palette.AccentText;
        _badgeText.Text = _isPinned ? "已固定" : "当前";
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Handled = true;
            Invoked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            e.Handled = true;
            Invoked?.Invoke(this, EventArgs.Empty);
            return;
        }

        base.OnKeyDown(e);
    }
}
