using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BingWallpaper.UI;

/// <summary>
/// Thumbnail grid of the last 8 days. Clicking an entry applies it immediately;
/// pictures that are not cached yet are downloaded on demand.
///
/// Bing serves at most 8 days, so the default size shows all of them at once. The
/// window is resizable anyway - the grid reflows, and a user with a small screen
/// gets a scroll bar rather than a window that does not fit.
/// </summary>
public sealed partial class HistoryWindow : Window
{
    private readonly AppController _controller;
    private readonly ObservableCollection<HistoryItem> _items = new ObservableCollection<HistoryItem>();

    private List<BingImageInfo> _images = new List<BingImageInfo>();
    private CancellationTokenSource? _thumbnailCts;
    private string _loadedSignature = string.Empty;
    private bool _busy;
    private bool _closingForGood;

    internal HistoryWindow(AppController controller)
    {
        _controller = controller;

        InitializeComponent();
        WindowSupport.Prepare(this, "选择日期", resizable: true);
        WindowSupport.ResizeLogical(this, 900, 640);
        WindowSupport.Center(this);
        AppWindow.Closing += OnClosing;

        ImageGrid.ItemsSource = _items;
        WindowSupport.ApplyTheme(this);
    }

    internal void ShowAndActivate() => WindowSupport.ShowAndActivate(this);

    internal void ApplyTheme() => WindowSupport.ApplyTheme(this);

    internal void CloseForGood()
    {
        _closingForGood = true;
        _thumbnailCts?.Cancel();
        try
        {
            Close();
        }
        catch (Exception ex)
        {
            Logger.Debug("Closing the history window failed: " + ex.Message);
        }
    }

    /// <summary>Shows the given metadata; fetches it when the caller has none yet.</summary>
    internal void LoadImages(IReadOnlyList<BingImageInfo> images)
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

    /// <summary>Called by the controller after a successful refresh.</summary>
    internal void OnImagesRefreshed(IReadOnlyList<BingImageInfo> images)
    {
        if (!AppWindow.IsVisible || BuildSignature(images) == _loadedSignature)
        {
            return;
        }

        Populate(images);
    }

    /// <summary>
    /// Called by the controller after the wallpaper or the pin changed, so the badge
    /// follows an action taken from the tray menu while this window is open.
    /// </summary>
    internal void RefreshCurrentMarker() => UpdateCurrentMarker();

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closingForGood)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
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

        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_controller.ShutdownToken);

        _items.Clear();
        for (int i = 0; i < _images.Count; i++)
        {
            _items.Add(new HistoryItem(i, _images[i]));
        }

        UpdateCurrentMarker();
        SetStatus("共 " + _images.Count + " 天，点击任意一张即可设为壁纸并固定。");
        _ = LoadThumbnailsAsync(_thumbnailCts.Token);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateCurrentMarker()
    {
        int current = _controller.CurrentIndex;
        bool pinned = _controller.IsPinned;
        foreach (HistoryItem item in _items)
        {
            bool isCurrent = item.Index == current;
            item.IsCurrent = isCurrent;
            item.IsPinned = isCurrent && pinned;
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryItem item)
        {
            _ = ApplyAsync(item.Index);
        }
    }

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

                if (cancellationToken.IsCancellationRequested || i >= _items.Count)
                {
                    return;
                }

                _items[i].Thumbnail = await DecodeAsync(bytes).ConfigureAwait(true);
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
    /// Turns downloaded bytes into something an Image can show. The bytes are copied
    /// into a WinRT stream first: BitmapImage decodes from an IRandomAccessStream,
    /// and it keeps reading from it after SetSourceAsync returns.
    /// </summary>
    private static async Task<BitmapImage> DecodeAsync(byte[] bytes)
    {
        InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream();
        using (DataWriter writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        BitmapImage bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
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
