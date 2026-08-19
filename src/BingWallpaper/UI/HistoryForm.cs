using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// Thumbnail grid of the last 8 days. Clicking an entry applies it immediately;
/// images that are not cached yet are downloaded on demand.
/// </summary>
internal sealed class HistoryForm : Form
{
    private static readonly Size ThumbnailSize = new(200, 120);

    private readonly TrayContext _context;
    private readonly ListView _list = new();
    private readonly ImageList _thumbnails = new();
    private readonly Label _status = new();

    private List<BingImageInfo> _images = new();
    private CancellationTokenSource? _thumbnailCts;
    private string _loadedSignature = string.Empty;
    private bool _busy;

    public HistoryForm(TrayContext context)
    {
        _context = context;

        Text = "BingWallpaper - 选择日期";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(760, 520);
        MinimumSize = new Size(520, 360);
        ShowInTaskbar = true;

        _thumbnails.ColorDepth = ColorDepth.Depth32Bit;
        _thumbnails.ImageSize = ThumbnailSize;

        _list.Dock = DockStyle.Fill;
        _list.View = View.LargeIcon;
        _list.LargeImageList = _thumbnails;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.MouseClick += OnListMouseClick;
        _list.KeyDown += OnListKeyDown;

        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(8, 0, 8, 0);
        _status.Text = "正在加载…";

        Controls.Add(_list);
        Controls.Add(_status);

        ThemeManager.ApplyToForm(this);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ThemeManager.ApplyToForm(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _thumbnailCts?.Cancel();
            _thumbnailCts?.Dispose();
            _thumbnails.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Shows the given metadata; fetches it when the caller has none yet.</summary>
    public void LoadImages(IReadOnlyList<BingImageInfo> images)
    {
        if (images.Count == 0)
        {
            _status.Text = "正在获取最近 8 天的壁纸信息…";
            _ = FetchAsync();
            return;
        }

        // Re-showing the window must not re-download every thumbnail.
        if (BuildSignature(images) == _loadedSignature)
        {
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
            Populate(images);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the wallpaper history.", ex);
            _status.Text = "获取壁纸列表失败，详见日志文件。";
        }
    }

    private static string BuildSignature(IReadOnlyList<BingImageInfo> images)
        => images.Count == 0 ? string.Empty : images.Count + ":" + images[0].StartDate + ":" + images[^1].StartDate;

    private void Populate(IReadOnlyList<BingImageInfo> images)
    {
        _images = new List<BingImageInfo>(images);
        _loadedSignature = BuildSignature(images);

        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = CancellationTokenSource.CreateLinkedTokenSource(_context.ShutdownToken);

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            _thumbnails.Images.Clear();
            foreach (BingImageInfo image in _images)
            {
                ListViewItem item = new(image.DisplayDate + Environment.NewLine + Shorten(image.DisplayTitle))
                {
                    ToolTipText = image.DisplayTitle + Environment.NewLine + image.Copyright,
                    ImageIndex = -1,
                };
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _list.ShowItemToolTips = true;
        _status.Text = "共 " + _images.Count + " 天，点击任意一张即可设为壁纸。";
        _ = LoadThumbnailsAsync(_thumbnailCts.Token);
    }

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
                byte[] bytes = await _context.Client
                    .DownloadBytesAsync(image.GetThumbnailUrl(), cancellationToken)
                    .ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested || IsDisposed || i >= _list.Items.Count)
                {
                    return;
                }

                using Bitmap thumbnail = CreateThumbnail(bytes);
                _thumbnails.Images.Add(thumbnail);
                _list.Items[i].ImageIndex = _thumbnails.Images.Count - 1;
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

    private static Bitmap CreateThumbnail(byte[] bytes)
    {
        using MemoryStream stream = new(bytes);
        using Image source = Image.FromStream(stream);
        Bitmap target = new(ThumbnailSize.Width, ThumbnailSize.Height);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, 0, 0, ThumbnailSize.Width, ThumbnailSize.Height);
        }

        return target;
    }

    private void OnListMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ListViewHitTestInfo hit = _list.HitTest(e.Location);
        if (hit.Item is not null)
        {
            _ = ApplyAsync(hit.Item.Index);
        }
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && _list.SelectedIndices.Count > 0)
        {
            _ = ApplyAsync(_list.SelectedIndices[0]);
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
        _status.Text = "正在应用 " + image.DisplayDate + " 的壁纸…";
        try
        {
            await _context.ApplyFromHistoryAsync(index).ConfigureAwait(true);
            _status.Text = "已应用：" + image.DisplayDate + "  " + image.DisplayTitle;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not apply the selected wallpaper.", ex);
            _status.Text = "应用失败，详见日志文件。";
        }
        finally
        {
            _busy = false;
        }
    }

    private static string Shorten(string value)
        => value.Length <= 18 ? value : value[..17] + "…";
}
