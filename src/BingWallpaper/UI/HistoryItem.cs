using System;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace BingWallpaper.UI;

/// <summary>
/// One tile of the history grid. Public because the compiled bindings of the item
/// template generate code against this type; everything it exposes is already
/// formatted for the screen, so no internal type leaks into the XAML.
/// </summary>
public sealed class HistoryItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isCurrent;
    private bool _isPinned;

    internal HistoryItem(int index, BingImageInfo info)
    {
        Index = index;
        Info = info;
        DateText = info.DisplayDate;
        TitleText = info.DisplayTitle;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Position in the metadata list - the index the controller applies by.</summary>
    public int Index { get; }

    public string DateText { get; }

    public string TitleText { get; }

    /// <summary>Null until the thumbnail has been downloaded.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value))
            {
                return;
            }

            _thumbnail = value;
            Raise(nameof(Thumbnail));
        }
    }

    /// <summary>Whether this picture is the one on the desktop right now.</summary>
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
            Raise(nameof(IsCurrent));
            Raise(nameof(BadgeVisibility));
        }
    }

    /// <summary>Whether it is also held against the refresh timer.</summary>
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
            Raise(nameof(IsPinned));
            Raise(nameof(BadgeText));
        }
    }

    /// <summary>The badge sits on the current tile and says whether it is pinned.</summary>
    public string BadgeText => IsPinned ? "已固定" : "当前";

    public Visibility BadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    internal BingImageInfo Info { get; }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
