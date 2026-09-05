using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// The wallpaper picker: the last eight days on one tab, the favourites on the other.
///
/// <para>
/// Both tabs are the same <see cref="TileGrid"/> with a different source behind it -
/// one drawing on the thumbnails Bing serves, one on the local disk cache. Only the
/// data differs; the painting, the hit testing and the keyboard belong to the grid.
/// </para>
/// <para>
/// The window is built fresh on every open and disposed when it closes, so nothing it
/// reads has to stay valid afterwards: the favourites are one directory listing at
/// open, and the titles are read the first time the tab is actually shown.
/// </para>
/// <para>
/// No WS_EX_COMPOSITED, which this window used to carry - and it must not come back.
/// That style composes the window and its children off screen and puts the result up
/// in one piece; its price is a slow scroll, which the old window never paid because,
/// sized to the whole eight day grid, it never scrolled. The favourites tab does, and
/// then the price is real: the view updates visibly late under the wheel and the
/// window can spin in WM_PAINT. Both flickers it was there for are gone by other
/// means - the grid is one double buffered control instead of a panel filling up with
/// tiles, and the status bar below draws itself the same way.
/// </para>
/// </summary>
internal sealed class PickerForm : Form
{
    /// <summary>
    /// The window opens on exactly this grid. Bing serves at most 8 days, so 4x2 shows
    /// all of them at once with no gap in the last row. It is the initial size only -
    /// the favourites tab is free to be resized.
    /// </summary>
    private const int Columns = 4;

    private const int Rows = 2;

    private const int RecentTab = 0;

    private const int FavoritesTab = 1;

    private readonly TrayContext _context;
    private readonly ThemedSegmentedControl _tabs = new ThemedSegmentedControl("最近", "收藏");
    private readonly ThemedSeparator _tabSeparator = new ThemedSeparator();
    private readonly TileGrid _grid = new TileGrid();
    private readonly ThemedSeparator _statusSeparator = new ThemedSeparator();
    private readonly ThemedStatusLabel _status = new ThemedStatusLabel();

    /// <summary>File names in favorites\, for the stars on the recent tab.</summary>
    private HashSet<string> _favoriteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private List<FavoriteItem> _favoriteItems = new List<FavoriteItem>();
    private List<BingImageInfo> _images = new List<BingImageInfo>();

    private RecentTileSource? _recent;
    private FavoriteTileSource? _favorites;
    private ThumbnailStore? _store;
    private ContextMenu? _menu;

    private string _loadedSignature = string.Empty;
    private string _statusText = "正在加载…";
    private bool _titlesLoaded;
    private bool _busy;

    public PickerForm(TrayContext context)
    {
        _context = context;

        ThemeManager.ApplySystemFont(this);

        Text = "选择壁纸";
        // Windows Forms does not inherit the icon of the executable: without this the
        // title bar and the task bar show the default .NET Framework window icon.
        Icon = AppIcon.Window;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ShowInTaskbar = true;

        // Fixed, as before: the window is exactly one grid wide, and resizing it could
        // only add empty space around the tiles. The favourites tab scrolls instead -
        // which is also why WS_EX_COMPOSITED is gone, see the class comment.
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        KeyPreview = true;

        _tabs.Dock = DockStyle.Top;
        _tabs.SelectedIndexChanged += (_, _) => ShowTab(_tabs.SelectedIndex);

        _tabSeparator.Dock = DockStyle.Top;

        _grid.Dock = DockStyle.Fill;
        _grid.ItemActivated += OnItemActivated;
        _grid.ItemMenuRequested += OnItemMenuRequested;
        _grid.HoveredIndexChanged += OnHoveredIndexChanged;

        _statusSeparator.Dock = DockStyle.Bottom;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.Padding = new Padding(8, 0, 8, 0);
        _status.Text = _statusText;

        // Docking is resolved from the last control backwards, so the status bar takes
        // the bottom edge, the tab strip the top one and the grid fills the rest.
        Controls.Add(_grid);
        Controls.Add(_tabSeparator);
        Controls.Add(_tabs);
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

        // Measured here rather than in the constructor: the strip measures itself
        // through DpiScale, and a size assigned before AutoScaleMode.Dpi has run would
        // be scaled a second time - the same reason SettingsForm sizes its drop downs
        // from OnLoad.
        _tabs.Height = _tabs.GetPreferredSize(Size.Empty).Height;

        FitToGrid();

        // FitToGrid runs after the window has already been placed: StartPosition is
        // resolved while the handle is created, against the size the form had then,
        // which is the WinForms default of 300x300. Resizing afterwards holds the top
        // left corner and grows towards the bottom right, so a grid this wide ends up
        // centred on a window that no longer exists and hanging off the screen edge.
        // Centre it again now that it is the size it will be shown at - still before
        // the reveal, so nothing is seen moving.
        CenterToScreen();

        // One listing, both tabs: the recent tab needs it for its stars and the
        // favourites tab for everything. Titles are not read here - see ShowTab.
        ScanFavorites();
    }

    /// <summary>
    /// Sizes the window to hold exactly <see cref="Columns"/> x <see cref="Rows"/>
    /// tiles plus the tab strip and the status bar. Everything is read at its final
    /// value rather than computed from the logical constants: the grid scales itself
    /// through DpiScale while the bars around it were scaled by AutoScaleMode.Dpi.
    /// </summary>
    private void FitToGrid()
    {
        // Room for a scroll bar on top of the four columns, whether one is showing or
        // not. Without it the favourites tab would lose a column to the bar the moment
        // it needs one - the window is a whole number of columns wide with nothing to
        // spare - and three columns in a window built for four looks like a bug. The
        // grid centres its content, so the spare width reads as margin either way.
        int width = (TileGrid.CellWidth * Columns)
            + (TileGrid.EdgePadding * 2)
            + SystemInformation.VerticalScrollBarWidth;
        int chrome = _tabs.Height + _tabSeparator.Height + _statusSeparator.Height + _status.Height;

        ClientSize = new Size(width, (TileGrid.CellHeight * Rows) + (TileGrid.EdgePadding * 2) + chrome);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _recent?.Dispose();
            _favorites?.Dispose();
            _store?.Dispose();
            _menu?.Dispose();
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
            _grid.Invalidate();
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
            Logger.Error("picker: loading the wallpaper list failed", ex);
            if (!IsDisposed)
            {
                SetStatus("获取壁纸列表失败，详见日志文件。");
            }
        }
    }

    /// <summary>
    /// Identifies what the recent tab is currently showing, so re-opening the window
    /// does not re-download every thumbnail. Dates alone are not enough: two markets
    /// serve the same eight days with different photos and different titles, and a
    /// signature built from the range only would call that unchanged and leave the old
    /// market on screen.
    /// </summary>
    private static string BuildSignature(IReadOnlyList<BingImageInfo> images)
    {
        if (images.Count == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new StringBuilder(images.Count * 48);
        foreach (BingImageInfo image in images)
        {
            builder.Append(image.StartDate).Append('|')
                .Append(image.ImageId).Append('|')
                .Append(image.Title).Append('\n');
        }

        return builder.ToString();
    }

    private void Populate(IReadOnlyList<BingImageInfo> images)
    {
        _images = new List<BingImageInfo>(images);
        _loadedSignature = BuildSignature(images);

        RecentTileSource? previous = _recent;
        _recent = new RecentTileSource(this, _images);
        if (_tabs.SelectedIndex == RecentTab)
        {
            _grid.SetSource(_recent);
        }

        previous?.Dispose();
        _recent.BeginLoading(_context.ShutdownToken);
        UpdateStatusForTab();
    }

    private void ShowTab(int index)
    {
        if (index == FavoritesTab)
        {
            EnsureFavoritesSource();
            _grid.SetSource(_favorites);
        }
        else
        {
            _grid.SetSource(_recent);
        }

        UpdateStatusForTab();
        _grid.Focus();
    }

    /// <summary>
    /// Builds the favourites view the first time the tab is shown. This is where
    /// favorites.txt is read - a window that was opened to glance at today's picture
    /// never touches it.
    /// </summary>
    private void EnsureFavoritesSource()
    {
        if (_favorites is not null)
        {
            return;
        }

        if (!_titlesLoaded)
        {
            Favorites.LoadTitles(_favoriteItems);
            _titlesLoaded = true;
        }

        _store = new ThumbnailStore(_grid, DpiScale.Round(TileGrid.TileWidth));
        _favorites = new FavoriteTileSource(this, _favoriteItems, _store);

        // Cache entries whose picture is gone are deleted whenever we happen to be
        // here anyway - the worker gets to it once the visible tiles are served.
        _store.RequestSweep(BuildNameList(_favoriteItems));
    }

    private void ScanFavorites()
    {
        _favoriteItems = Favorites.Scan();
        _favoriteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FavoriteItem item in _favoriteItems)
        {
            _favoriteNames.Add(item.FileName);
        }

        _titlesLoaded = false;
    }

    /// <summary>Re-reads the folder after a change of our own, or on F5.</summary>
    private void ReloadFavorites(bool keepPosition)
    {
        ScanFavorites();
        if (_favorites is not null)
        {
            Favorites.LoadTitles(_favoriteItems);
            _titlesLoaded = true;
            _favorites.SetItems(_favoriteItems);
            _grid.Reload(keepPosition);
        }

        UpdateStatusForTab();
        _grid.Invalidate();
    }

    private static List<string> BuildNameList(List<FavoriteItem> items)
    {
        List<string> names = new List<string>(items.Count);
        foreach (FavoriteItem item in items)
        {
            names.Add(item.FileName);
        }

        return names;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5 && _tabs.SelectedIndex == FavoritesTab)
        {
            ReloadFavorites(keepPosition: true);
            return true;
        }

        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// The wheel goes to whichever control has the focus - which right after the
    /// window opens is the tab strip - so it is handed to the grid from here rather
    /// than left to reach it by accident.
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_grid.Bounds.Contains(PointToClient(MousePosition)))
        {
            _grid.ScrollByWheel(e.Delta);
            return;
        }

        base.OnMouseWheel(e);
    }

    /// <summary>Remembers the text so hovering a tile can restore it afterwards.</summary>
    private void SetStatus(string text)
    {
        _statusText = text;
        _status.Text = text;
    }

    private void UpdateStatusForTab()
    {
        if (_tabs.SelectedIndex == FavoritesTab)
        {
            long bytes = 0;
            foreach (FavoriteItem item in _favoriteItems)
            {
                bytes += item.Length;
            }

            SetStatus(_favoriteItems.Count == 0
                ? "收藏夹是空的。在「最近」里右键任意一张即可收藏。"
                : "共 " + _favoriteItems.Count.ToString(CultureInfo.InvariantCulture) +
                  " 张，占用 " + FormatSize(bytes) + "。");
            return;
        }

        SetStatus(_images.Count == 0
            ? "正在获取最近 8 天的壁纸信息…"
            : "共 " + _images.Count.ToString(CultureInfo.InvariantCulture) + " 天，点击任意一张即可设为壁纸并锁定。");
    }

    private static string FormatSize(long bytes)
    {
        const double Mib = 1024 * 1024;
        double megabytes = bytes / Mib;
        return megabytes >= 1024
            ? (megabytes / 1024).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
            : megabytes.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
    }

    /// <summary>
    /// Called by the tray context after the wallpaper or the pin changed, so the badge
    /// follows an action taken from the tray menu while this window is open.
    /// </summary>
    public void RefreshCurrentMarker()
    {
        if (!IsDisposed)
        {
            _grid.Invalidate();
        }
    }

    /// <summary>The badge a picture carries, or null when it carries none.</summary>
    private string? GetBadge(string fileName)
    {
        string? applied = _context.AppliedFileName;
        if (applied is null || !string.Equals(applied, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _context.IsPinned ? "已锁定" : "当前";
    }

    private bool IsFavorite(string fileName) => _favoriteNames.Contains(fileName);

    /// <summary>
    /// The status bar doubles as the place where a title is shown in full - a tile has
    /// to cut long ones off at its own width.
    /// </summary>
    private void OnHoveredIndexChanged(object? sender, EventArgs e)
    {
        int index = _grid.HoveredIndex;
        ITileSource? source = CurrentSource;
        if (index < 0 || source is null || index >= source.Count)
        {
            _status.Text = _statusText;
            return;
        }

        TileInfo info = source.GetInfo(index);
        _status.Text = info.Date + " · " + info.Title;
    }

    private ITileSource? CurrentSource => _tabs.SelectedIndex == FavoritesTab ? _favorites : _recent;

    private void OnItemActivated(object? sender, TileEventArgs e)
    {
        if (_tabs.SelectedIndex == FavoritesTab)
        {
            ApplyFavorite(e.Index);
            return;
        }

        StartApplyRecent(e.Index);
    }

    private void StartApplyRecent(int index) => _ = ApplyRecentAsync(index);

    private void StartFavorite(int index) => _ = FavoriteAsync(index);

    private async Task ApplyRecentAsync(int index)
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
            await _context.ApplyFromPickerAsync(index).ConfigureAwait(true);
            SetStatus((_context.IsPinned ? "已锁定：" : "已应用：") + image.DisplayLine);
        }
        catch (Exception ex)
        {
            Logger.Error("picker: applying the selected wallpaper failed", ex);
            SetStatus("应用失败，详见日志文件。");
        }
        finally
        {
            _busy = false;
            _grid.Invalidate();
        }
    }

    private void ApplyFavorite(int index)
    {
        if (_busy || index < 0 || index >= _favoriteItems.Count)
        {
            return;
        }

        _busy = true;
        FavoriteItem item = _favoriteItems[index];
        try
        {
            SetStatus(_context.ApplyFavorite(item.FileName)
                ? "已锁定：" + item.DisplayDate + " · " + item.Title
                : "应用失败，详见日志文件。");
        }
        finally
        {
            _busy = false;
            _grid.Invalidate();
        }
    }

    private void OnItemMenuRequested(object? sender, TileEventArgs e)
    {
        if (e.Index < 0)
        {
            return;
        }

        _menu?.Dispose();
        _menu = _tabs.SelectedIndex == FavoritesTab
            ? BuildFavoriteMenu(e.Index)
            : BuildRecentMenu(e.Index);

        _menu?.Show(_grid, e.Location);
    }

    private ContextMenu? BuildRecentMenu(int index)
    {
        if (index >= _images.Count)
        {
            return null;
        }

        BingImageInfo image = _images[index];
        string fileName = image.GetFileName(_context.Config.Resolution);
        bool favorited = IsFavorite(fileName);

        MenuItem favorite = favorited
            ? new MenuItem("取消收藏", (_, _) => Unfavorite(fileName))
            : new MenuItem("收藏", (_, _) => StartFavorite(index));

        MenuItem link = new MenuItem("在必应中查看", (_, _) => OpenLink(image.CopyrightLink))
        {
            Enabled = !string.IsNullOrWhiteSpace(image.CopyrightLink),
        };

        return new ContextMenu(new[]
        {
            // Bold, the way the shell marks a menu's default action: this row and a
            // click on the tile are the same command, and the emphasis is what says so.
            new MenuItem("设为壁纸并锁定", (_, _) => StartApplyRecent(index)) { DefaultItem = true },
            favorite,
            link,
        });
    }

    /// <summary>
    /// Two menus, by what the entry is. A picture the user copied in gets no "un-
    /// favourite": there is nowhere to move it to. wallpapers\ is the retention pass's
    /// territory, and a file that carries the user's own last write time would be
    /// deleted by the next pass - or, if it is a .png, never matched by it and left
    /// behind invisibly. Explorer is the honest answer: their file, their move.
    /// </summary>
    private ContextMenu? BuildFavoriteMenu(int index)
    {
        if (index >= _favoriteItems.Count)
        {
            return null;
        }

        FavoriteItem item = _favoriteItems[index];
        string path = Path.Combine(Paths.FavoritesDirectory, item.FileName);

        List<MenuItem> items = new List<MenuItem>(4)
        {
            new MenuItem("设为壁纸并锁定", (_, _) => ApplyFavorite(index)) { DefaultItem = true },
        };

        if (item.IsBingImage)
        {
            items.Add(new MenuItem("取消收藏", (_, _) => Unfavorite(item.FileName)));
        }

        items.Add(new MenuItem("打开文件所在位置", (_, _) => ShowInExplorer(path)));

        // Last on purpose: the only row here that can be greyed out, and the least
        // used - the rows that always work come first.
        if (item.IsBingImage)
        {
            items.Add(new MenuItem("在必应中查看", (_, _) => OpenLink(item.CopyrightLink))
            {
                Enabled = !string.IsNullOrWhiteSpace(item.CopyrightLink),
            });
        }

        return new ContextMenu(items.ToArray());
    }

    /// <summary>
    /// Favourites a picture from the recent tab. The file has to exist before it can
    /// be moved, and a day nobody applied yet is only on Bing's servers - so the
    /// download that applying would have done happens here instead.
    /// </summary>
    private async Task FavoriteAsync(int index)
    {
        if (_busy || index < 0 || index >= _images.Count)
        {
            return;
        }

        _busy = true;
        BingImageInfo image = _images[index];
        string fileName = image.GetFileName(_context.Config.Resolution);
        try
        {
            if (!File.Exists(Paths.ResolveWallpaperFile(fileName)))
            {
                SetStatus("正在下载 " + image.DisplayDate + " 的原图…");
                await _context.EnsureCachedAsync(image).ConfigureAwait(true);
            }

            if (IsDisposed)
            {
                return;
            }

            if (Favorites.Add(fileName, image.DisplayTitle, image.CopyrightLink))
            {
                _context.NotifyWallpaperMoved(fileName);
                ReloadFavorites(keepPosition: true);
                SetStatus("已收藏：" + image.DisplayLine);
            }
            else
            {
                SetStatus("收藏失败，详见日志文件。");
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error("picker: favouriting failed file=" + fileName, ex);
            if (!IsDisposed)
            {
                SetStatus("收藏失败，详见日志文件。");
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void Unfavorite(string fileName)
    {
        if (!Favorites.Remove(fileName))
        {
            SetStatus("取消收藏失败，详见日志文件。");
            return;
        }

        _context.NotifyWallpaperMoved(fileName);
        ReloadFavorites(keepPosition: true);
        SetStatus("已取消收藏，图片回到最近缓存中。");
    }

    private static void OpenLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Info("shell: opened image source url=" + url);
        }
        catch (Exception ex)
        {
            Logger.Error("shell: opening the image source failed", ex);
        }
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            // /select needs the path quoted and the comma right after the switch;
            // Explorer parses this one itself rather than through the usual rules.
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Error("shell: showing the file in explorer failed", ex);
        }
    }

    /// <summary>
    /// The last eight days. The bitmaps come from the shared byte cache one step above
    /// this window, so re-opening it does not re-download anything.
    /// </summary>
    private sealed class RecentTileSource : ITileSource, IDisposable
    {
        private readonly PickerForm _owner;
        private readonly List<BingImageInfo> _images;
        private readonly string[] _fileNames;
        private readonly Bitmap?[] _bitmaps;
        private readonly bool[] _failed;

        private CancellationTokenSource? _cts;

        public RecentTileSource(PickerForm owner, List<BingImageInfo> images)
        {
            _owner = owner;
            _images = images;
            _bitmaps = new Bitmap?[images.Count];
            _failed = new bool[images.Count];

            // Fixed for the life of the source - the resolution can only change by way
            // of a refresh, which builds a new one.
            _fileNames = new string[images.Count];
            for (int i = 0; i < images.Count; i++)
            {
                _fileNames[i] = images[i].GetFileName(owner._context.Config.Resolution);
            }
        }

        public event Action<int>? TileChanged;

        public int Count => _images.Count;

        public string EmptyText => "尚未获取到壁纸信息。";

        public TileInfo GetInfo(int index)
        {
            BingImageInfo image = _images[index];
            string fileName = _fileNames[index];
            return new TileInfo(
                image.DisplayDate,
                image.DisplayTitle,
                _owner.GetBadge(fileName),
                _owner.IsFavorite(fileName),
                _bitmaps[index],
                _failed[index]);
        }

        /// <summary>
        /// Nothing to do: eight entries all fit on screen, and their thumbnails are a
        /// few tens of KB each. The window this interface exists for is the favourites'.
        /// </summary>
        public void SetWindow(int first, int count)
        {
        }

        public void BeginLoading(CancellationToken shutdown)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            _ = LoadAsync(_cts.Token);
        }

        private async Task LoadAsync(CancellationToken cancellationToken)
        {
            for (int i = 0; i < _images.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested || _owner.IsDisposed)
                {
                    return;
                }

                try
                {
                    byte[] bytes = await _owner._context.Thumbnails
                        .GetAsync(_images[i].GetThumbnailUrl(), cancellationToken)
                        .ConfigureAwait(true);

                    if (cancellationToken.IsCancellationRequested || _owner.IsDisposed)
                    {
                        return;
                    }

                    // Decoded to the width of the box that paints it: Bing serves
                    // 400x240, which as a bitmap costs four times what a tile sized one
                    // does, and the scaling then happens once instead of once a repaint.
                    _bitmaps[i] = ThumbnailStore.Decode(bytes, DpiScale.Round(TileGrid.TileWidth));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _failed[i] = true;
                    Logger.Warn("picker: thumbnail load failed startdate=" + _images[i].StartDate + " error=" + ex.Message);
                }

                TileChanged?.Invoke(i);
            }
        }

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            for (int i = 0; i < _bitmaps.Length; i++)
            {
                _bitmaps[i]?.Dispose();
                _bitmaps[i] = null;
            }
        }
    }

    /// <summary>
    /// The favourites. Everything on screen comes from the directory listing taken when
    /// the window opened; the bitmaps are held by <see cref="ThumbnailStore"/> for the
    /// visible range only, which is what keeps the memory flat as the folder grows.
    /// </summary>
    private sealed class FavoriteTileSource : ITileSource, IDisposable
    {
        private readonly PickerForm _owner;
        private readonly ThumbnailStore _store;

        /// <summary>File names of the range currently declared to the store.</summary>
        private readonly List<string> _window = new List<string>();

        private List<FavoriteItem> _items;
        private int _windowFirst;

        public FavoriteTileSource(PickerForm owner, List<FavoriteItem> items, ThumbnailStore store)
        {
            _owner = owner;
            _items = items;
            _store = store;
            _store.Ready += OnThumbnailReady;
        }

        public event Action<int>? TileChanged;

        public int Count => _items.Count;

        public string EmptyText => "收藏夹是空的。\r\n在「最近」里右键任意一张即可收藏，也可以把图片直接拷进 wallpapers\\favorites\\。";

        public TileInfo GetInfo(int index)
        {
            FavoriteItem item = _items[index];
            Bitmap? bitmap = _store.Get(item.FileName, out bool failed);
            return new TileInfo(
                item.DisplayDate,
                item.Title,
                _owner.GetBadge(item.FileName),

                // No star here: everything on this tab is a favourite, so a mark on
                // every tile would say nothing.
                starred: false,
                bitmap,
                failed);
        }

        public void SetItems(List<FavoriteItem> items)
        {
            _items = items;
            _window.Clear();
            _windowFirst = 0;
        }

        public void SetWindow(int first, int count)
        {
            _windowFirst = first;
            _window.Clear();
            for (int i = first; i < first + count && i < _items.Count; i++)
            {
                _window.Add(_items[i].FileName);
            }

            _store.SetWindow(_window);
        }

        /// <summary>
        /// Searched in the window rather than in the whole list: the answer can only be
        /// in the range that was asked for, and that range is a screen or three - not
        /// the several thousand entries the list may hold.
        /// </summary>
        private void OnThumbnailReady(string fileName)
        {
            for (int i = 0; i < _window.Count; i++)
            {
                if (string.Equals(_window[i], fileName, StringComparison.OrdinalIgnoreCase))
                {
                    TileChanged?.Invoke(_windowFirst + i);
                    return;
                }
            }
        }

        public void Dispose() => _store.Ready -= OnThumbnailReady;
    }
}
