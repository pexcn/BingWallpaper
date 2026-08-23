using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Thumbnail grid of the last 8 days. Clicking an entry applies it immediately;
/// images that are not cached yet are downloaded on demand.
///
/// The grid is a FlowLayoutPanel of owner drawn tiles rather than a ListView: see
/// <see cref="ThumbnailTile"/> for what the ListView decided on its own.
/// </summary>
internal sealed class HistoryForm : Form
{
    /// <summary>
    /// The window is sized to exactly this grid. Bing serves at most 8 days, so
    /// 4x2 shows all of them at once: no scroll bar, and no gap in the last row.
    /// </summary>
    private const int Columns = 4;

    private const int Rows = 2;

    private readonly TrayContext _context;
    private readonly FlowLayoutPanel _grid = new();
    private readonly ThemedSeparator _statusSeparator = new();
    private readonly Label _status = new();
    private readonly List<ThumbnailTile> _tiles = new();

    private List<BingImageInfo> _images = new();
    private CancellationTokenSource? _thumbnailCts;
    private string _loadedSignature = string.Empty;
    private string _statusText = "正在加载…";
    private bool _busy;

    public HistoryForm(TrayContext context)
    {
        _context = context;

        ThemeManager.ApplySystemFont(this);

        Text = "选择日期";
        // Windows Forms does not inherit the icon of the executable: without this the
        // title bar and the task bar show the default .NET Framework window icon.
        Icon = AppIcon.Window;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;
        // Fixed: the window is exactly one grid wide and tall, so there is nothing
        // for a resize or a maximise to do except add empty space around the tiles.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;

        _grid.Dock = DockStyle.Fill;
        _grid.AutoScroll = true;
        _grid.WrapContents = true;
        _grid.FlowDirection = FlowDirection.LeftToRight;
        _grid.Padding = new Padding(8);

        _statusSeparator.Dock = DockStyle.Bottom;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(8, 0, 8, 0);
        _status.Text = _statusText;

        // Docking is resolved from the last control backwards, so the status bar
        // takes the bottom edge, the rule sits above it and the grid fills the rest.
        Controls.Add(_grid);
        Controls.Add(_statusSeparator);
        Controls.Add(_status);

        ThemeManager.ApplyToForm(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyToForm(this);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        FitToGrid();

        // FitToGrid runs after the window has already been placed: StartPosition is
        // resolved while the handle is created, against the size the form had then,
        // which is the WinForms default of 300x300. Resizing afterwards holds the top
        // left corner and grows towards the bottom right, so a grid this wide ends up
        // centred on a window that no longer exists and hanging off the screen edge.
        // Centre it again now that it is the size it will be shown at - still before
        // the reveal, so nothing is seen moving.
        CenterToScreen();
    }

    /// <summary>
    /// Sizes the window to hold exactly <see cref="Columns"/> x <see cref="Rows"/>
    /// tiles. The measurements are read back from the controls instead of computed
    /// from the logical constants, because the tiles scale themselves through
    /// DpiScale while the panels around them were scaled by AutoScaleMode.Dpi.
    /// </summary>
    private void FitToGrid()
    {
        int margin = DpiScale.Round(ThumbnailTile.TileMargin) * 2;
        int cell = DpiScale.Round(ThumbnailTile.TileWidth) + margin;
        int row = DpiScale.Round(ThumbnailTile.TileHeight) + margin;

        ClientSize = new Size(
            (cell * Columns) + _grid.Padding.Horizontal,
            (row * Rows) + _grid.Padding.Vertical + _statusSeparator.Height + _status.Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
        }

        base.Dispose(disposing);
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

    /// <summary>Called by the tray context after a successful refresh.</summary>
    public void OnImagesRefreshed(IReadOnlyList<BingImageInfo> images)
    {
        if (IsDisposed || !Visible || BuildSignature(images) == _loadedSignature)
        {
            return;
        }

        Populate(images);
    }

    private async Task FetchAsync()
    {
        try
        {
            List<BingImageInfo> images = await _context.Client
                .FetchAsync(_context.Config.Market, 0, BingClient.MaxImageCount, _context.ShutdownToken)
                .ConfigureAwait(true);

            // Keep the tray menu and this window on the same list, otherwise the
            // indices used for applying an image would not match. Worth doing even
            // when the window is gone: the tray menu is the other reader of that list.
            _context.AdoptImages(images);

            // Closing this window disposes it, and that can happen while the fetch is
            // still in flight - there is nothing left to populate.
            if (!IsDisposed)
            {
                Populate(images);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the wallpaper history.", ex);
            if (!IsDisposed)
            {
                SetStatus("获取壁纸列表失败，详见日志文件。");
            }
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

        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_context.ShutdownToken);

        _grid.SuspendLayout();
        try
        {
            foreach (ThumbnailTile tile in _tiles)
            {
                _grid.Controls.Remove(tile);
                tile.Dispose();
            }

            _tiles.Clear();

            for (int i = 0; i < _images.Count; i++)
            {
                ThumbnailTile tile = new ThumbnailTile(i, _images[i]);
                tile.Click += OnTileClick;
                tile.MouseEnter += OnTileMouseEnter;
                tile.MouseLeave += OnTileMouseLeave;
                _tiles.Add(tile);
                _grid.Controls.Add(tile);
            }
        }
        finally
        {
            _grid.ResumeLayout(performLayout: true);
        }

        // The tiles were created after the window was themed, so colour them now.
        ThemeManager.ApplyToControl(_grid);
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

    /// <summary>
    /// Called by the tray context after the wallpaper or the pin changed, so the
    /// badge follows an action taken from the tray menu while this window is open.
    /// </summary>
    public void RefreshCurrentMarker()
    {
        if (!IsDisposed)
        {
            UpdateCurrentMarker();
        }
    }

    private void UpdateCurrentMarker()
    {
        int current = _context.CurrentIndex;
        bool pinned = _context.IsPinned;
        foreach (ThumbnailTile tile in _tiles)
        {
            bool isCurrent = tile.Index == current;
            tile.IsCurrent = isCurrent;
            tile.IsPinned = isCurrent && pinned;
        }
    }

    private void OnTileClick(object? sender, EventArgs e)
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
    private void OnTileMouseEnter(object? sender, EventArgs e)
    {
        if (sender is ThumbnailTile tile)
        {
            _status.Text = tile.Info.DisplayLine;
        }
    }

    private void OnTileMouseLeave(object? sender, EventArgs e) => _status.Text = _statusText;

    private async Task LoadThumbnailsAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < _images.Count; i++)
        {
            if (cancellationToken.IsCancellationRequested || IsDisposed)
            {
                return;
            }

            BingImageInfo image = _images[i];
            try
            {
                byte[] bytes = await _context.Thumbnails
                    .GetAsync(image.GetThumbnailUrl(), cancellationToken)
                    .ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested || IsDisposed || i >= _tiles.Count)
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
    /// Copies the bytes into a standalone bitmap. Image.FromStream keeps using the
    /// stream it was given, which is closed as soon as this method returns, and the
    /// tile scales the picture to its own box while painting.
    /// </summary>
    private static Bitmap Decode(byte[] bytes)
    {
        using (MemoryStream stream = new MemoryStream(bytes))
        using (Image source = Image.FromStream(stream))
        {
            return new Bitmap(source);
        }
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
            await _context.ApplyFromHistoryAsync(index).ConfigureAwait(true);
            SetStatus((_context.IsPinned ? "已固定：" : "已应用：") + image.DisplayLine);
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
