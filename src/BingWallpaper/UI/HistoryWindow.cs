using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Thumbnail grid of the last 8 days. Clicking an entry applies it immediately;
/// images that are not cached yet are downloaded on demand.
/// </summary>
internal sealed class HistoryWindow : AppWindow
{
    /// <summary>
    /// The window is sized to exactly this grid. Bing serves at most 8 days, so
    /// 4x2 shows all of them at once: no scroll bar, and no gap in the last row.
    /// </summary>
    private const int Columns = 4;

    private readonly TrayController _controller;
    private readonly UniformGrid _grid = new() { Columns = Columns };
    private readonly Border _statusSeparator = new() { Height = 1 };
    private readonly TextBlock _status = new();
    private readonly List<ThumbnailTile> _tiles = new();

    private List<BingImageInfo> _images = new();
    private CancellationTokenSource? _thumbnailCts;
    private string _loadedSignature = string.Empty;
    private string _statusText = "正在加载…";
    private bool _busy;

    public HistoryWindow(TrayController controller)
        : base("选择日期")
    {
        _controller = controller;

        // The window is exactly one grid wide and tall, so there is nothing for a
        // resize to do except add empty space around the tiles.
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        _grid.Margin = new Thickness(8);

        _status.Text = _statusText;
        _status.Margin = new Thickness(12, 6, 12, 8);
        _status.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
        _status.VerticalAlignment = VerticalAlignment.Center;

        StackPanel root = new StackPanel { Orientation = Orientation.Vertical };
        root.Children.Add(_grid);
        root.Children.Add(_statusSeparator);
        root.Children.Add(_status);
        Content = root;

        ApplyPalette();
    }

    /// <summary>Shows the given metadata; fetches it when the caller has none yet.</summary>
    public void LoadImages(IReadOnlyList<BingImageInfo> images)
    {
        if (images.Count == 0)
        {
            SetStatus("正在获取最近 8 天的壁纸信息…");
            _ = FetchAsync();
            return;
        }

        // Re-showing the window must not re-download every thumbnail.
        if (BuildSignature(images) == _loadedSignature)
        {
            UpdateCurrentMarker();
            return;
        }

        Populate(images);
    }

    /// <summary>Called by the tray controller after a successful refresh.</summary>
    public void OnImagesRefreshed(IReadOnlyList<BingImageInfo> images)
    {
        if (!IsVisible || BuildSignature(images) == _loadedSignature)
        {
            return;
        }

        Populate(images);
    }

    /// <summary>
    /// Called by the tray controller after the wallpaper or the pin changed, so the
    /// badge follows an action taken from the tray menu while this window is open.
    /// </summary>
    public void RefreshCurrentMarker() => UpdateCurrentMarker();

    /// <summary>Stops the thumbnail downloads; called when the program exits.</summary>
    public void CancelPendingWork()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    protected override void OnThemeChanged()
    {
        ApplyPalette();
        foreach (ThumbnailTile tile in _tiles)
        {
            tile.ApplyTheme();
        }
    }

    private void ApplyPalette()
    {
        ThemePalette palette = ThemeManager.Palette;
        _statusSeparator.Background = palette.Border;
        _status.Foreground = palette.SecondaryText;
    }

    private async Task FetchAsync()
    {
        try
        {
            List<BingImageInfo> images = await _controller.Client
                .FetchAsync(_controller.Config.Market, 0, BingClient.MaxImageCount, _controller.ShutdownToken)
                .ConfigureAwait(true);

            // Keep the tray menu and this window on the same list, otherwise the
            // indices used for applying an image would not match.
            _controller.AdoptImages(images);
            Populate(images);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the wallpaper history.", ex);
            SetStatus("获取壁纸列表失败，详见日志文件。");
        }
    }

    private static string BuildSignature(IReadOnlyList<BingImageInfo> images)
        => images.Count == 0
            ? string.Empty
            : images.Count + ":" + images[0].StartDate + ":" + images[images.Count - 1].StartDate;

    private void Populate(IReadOnlyList<BingImageInfo> images)
    {
        _images = new List<BingImageInfo>(images);
        _loadedSignature = BuildSignature(images);

        CancelPendingWork();
        _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_controller.ShutdownToken);

        foreach (ThumbnailTile tile in _tiles)
        {
            _grid.Children.Remove(tile);
            tile.Thumbnail = null;
        }

        _tiles.Clear();

        for (int i = 0; i < _images.Count; i++)
        {
            ThumbnailTile tile = new ThumbnailTile(i, _images[i]);
            tile.Invoked += OnTileInvoked;
            tile.PointerEntered += OnTilePointerEntered;
            tile.PointerExited += OnTilePointerExited;
            _tiles.Add(tile);
            _grid.Children.Add(tile);
        }

        UpdateCurrentMarker();
        SetStatus("共 " + _images.Count + " 天，点击任意一张即可设为壁纸并固定。");
        _ = LoadThumbnailsAsync(_thumbnailCts.Token);
    }

    /// <summary>Remembers the text so hovering a tile can restore it afterwards.</summary>
    private void SetStatus(string text)
    {
        _statusText = text;
        _status.Text = text;
    }

    private void UpdateCurrentMarker()
    {
        int current = _controller.CurrentIndex;
        bool pinned = _controller.IsPinned;
        foreach (ThumbnailTile tile in _tiles)
        {
            bool isCurrent = tile.Index == current;
            tile.IsCurrent = isCurrent;
            tile.IsPinned = isCurrent && pinned;
        }
    }

    private void OnTileInvoked(object? sender, EventArgs e)
    {
        if (sender is ThumbnailTile tile)
        {
            _ = ApplyAsync(tile.Index);
        }
    }

    /// <summary>
    /// The status bar doubles as the place where a title is shown in full - the tile
    /// itself has to cut long ones off at its own width.
    /// </summary>
    private void OnTilePointerEntered(object? sender, EventArgs e)
    {
        if (sender is ThumbnailTile tile)
        {
            _status.Text = tile.Info.DisplayLine;
        }
    }

    private void OnTilePointerExited(object? sender, EventArgs e) => _status.Text = _statusText;

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < _images.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            BingImageInfo image = _images[i];
            try
            {
                byte[] bytes = await _controller.Client
                    .DownloadBytesAsync(image.GetThumbnailUrl(), cancellationToken)
                    .ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested || i >= _tiles.Count)
                {
                    return;
                }

                _tiles[i].Thumbnail = Decode(bytes);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not load the thumbnail for " + image.StartDate + ": " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Decodes the bytes into a bitmap. Avalonia reads the stream out completely
    /// while the bitmap is being constructed, so closing it right after is safe.
    /// </summary>
    private static Bitmap Decode(byte[] bytes)
    {
        using MemoryStream stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    private async Task ApplyAsync(int index)
    {
        if (_busy || index < 0 || index >= _images.Count)
        {
            return;
        }

        _busy = true;
        BingImageInfo image = _images[index];
        SetStatus("正在应用 " + image.DisplayDate + " 的壁纸…");
        try
        {
            await _controller.ApplyFromHistoryAsync(index).ConfigureAwait(true);
            SetStatus((_controller.IsPinned ? "已固定：" : "已应用：") + image.DisplayLine);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply the selected wallpaper.", ex);
            SetStatus("应用失败，详见日志文件。");
        }
        finally
        {
            _busy = false;
            UpdateCurrentMarker();
        }
    }
}
