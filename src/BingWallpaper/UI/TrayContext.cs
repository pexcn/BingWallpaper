using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BingWallpaper.Theme;

namespace BingWallpaper.UI;

/// <summary>
/// The application itself: a tray icon, a timer and the refresh logic.
/// There is no main window - the message loop is hosted by an
/// <see cref="ApplicationContext"/> plus a hidden window used for broadcasts.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly BingClient _client = new();
    private readonly HiddenWindow _window;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;

    private readonly ToolStripMenuItem _titleItem;
    private readonly ToolStripMenuItem _newerItem;
    private readonly ToolStripMenuItem _olderItem;
    private readonly ToolStripMenuItem _historyItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _folderItem;
    private readonly ToolStripMenuItem _settingsItem;
    private readonly ToolStripMenuItem _exitItem;

    private readonly CancellationTokenSource _shutdown = new();

    private List<BingImageInfo> _images = new();
    private int _currentIndex;
    private string? _appliedPath;
    private BingImageInfo? _appliedImage;
    private SettingsForm? _settingsForm;
    private HistoryForm? _historyForm;
    private bool _busy;
    private bool _disposed;

    public TrayContext(AppConfig config)
    {
        _config = config;

        _window = new HiddenWindow();
        _window.SystemColorSchemeChanged += (_, _) => ThemeManager.HandleSystemThemeChanged();

        // The title row doubles as the "open the image source" command. It has to be
        // enabled to raise Click at all, so it only greys out - and reads as a plain
        // header - while there is no link behind it.
        _titleItem = new ToolStripMenuItem("正在获取今日壁纸…", null, (_, _) => OpenCopyrightLink())
        {
            Enabled = false,
            ToolTipText = "查看图片来源",
        };
        _newerItem = new ToolStripMenuItem("下一张", null, (_, _) => MoveBy(-1)) { Enabled = false };
        _olderItem = new ToolStripMenuItem("上一张", null, (_, _) => MoveBy(1)) { Enabled = false };
        _historyItem = new ToolStripMenuItem("选择日期…", null, (_, _) => ShowHistory());
        _refreshItem = new ToolStripMenuItem("立即刷新", null, (_, _) => StartRefresh(userInitiated: true));
        _folderItem = new ToolStripMenuItem("打开壁纸文件夹", null, (_, _) => OpenWallpaperFolder());
        _settingsItem = new ToolStripMenuItem("设置…", null, (_, _) => ShowSettings());
        _exitItem = new ToolStripMenuItem("退出", null, (_, _) => ExitApplication());

        _menu = new ContextMenuStrip();
        Font? menuFont = SystemFonts.MenuFont;
        if (menuFont is not null)
        {
            _menu.Font = menuFont;
        }

        _menu.Items.AddRange(new ToolStripItem[]
        {
            _titleItem,
            new ToolStripSeparator(),
            _olderItem,
            _newerItem,
            _historyItem,
            _refreshItem,
            new ToolStripSeparator(),
            _folderItem,
            new ToolStripSeparator(),
            _settingsItem,
            _exitItem,
        });

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = "必应壁纸",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        _timer = new System.Windows.Forms.Timer { Interval = GetIntervalMilliseconds() };
        _timer.Tick += (_, _) => StartRefresh(userInitiated: false);
        _timer.Start();

        ThemeManager.ThemeChanged += OnThemeChanged;
        ThemeManager.ApplyToMenu(_menu);

        // Run the first check as soon as the message loop starts.
        _window.BeginInvoke(new Action(() => StartRefresh(userInitiated: false)));
    }

    /// <summary>Metadata of the last 8 days, newest first.</summary>
    public IReadOnlyList<BingImageInfo> Images => _images;

    public AppConfig Config => _config;

    public BingClient Client => _client;

    public int CurrentIndex => _currentIndex;

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Called from the single instance listener thread.</summary>
    public void RequestActivation()
    {
        try
        {
            _window.BeginInvoke(new Action(ShowSettings));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not activate the settings window: " + ex.Message);
        }
    }

    /// <summary>
    /// Shares metadata that the history window fetched on its own, so both windows
    /// index into the same list.
    /// </summary>
    public void AdoptImages(List<BingImageInfo> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        _images = images;
        UpdateMenuState();
    }

    /// <summary>Downloads (if needed) and applies the image at <paramref name="index"/>.</summary>
    public async Task ApplyIndexAsync(int index, bool force)
    {
        if (index < 0 || index >= _images.Count)
        {
            Logger.Warn("Requested image index " + index + " is out of range (" + _images.Count + " entries).");
            return;
        }

        BingImageInfo image = _images[index];
        Paths.EnsureWallpaperDirectory();
        string path = Path.Combine(
            Paths.WallpaperDirectory,
            image.GetFileName(_config.Resolution));

        bool cached = File.Exists(path) && new FileInfo(path).Length > 0;
        if (cached)
        {
            Logger.Info("Cache hit: " + path);
        }
        else
        {
            await _client
                .DownloadImageAsync(image.GetImageUrl(_config.Resolution), path, _shutdown.Token)
                .ConfigureAwait(true);
        }

        if (!force && cached && IsCurrentWallpaper(path))
        {
            Logger.Info("Wallpaper is already up to date, nothing to do.");
        }
        else
        {
            WallpaperService.Apply(path, _config.Fit);
        }

        _currentIndex = index;
        _appliedPath = path;
        _appliedImage = image;
        UpdateMenuState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            try
            {
                _shutdown.Cancel();
            }
            catch (Exception ex)
            {
                Logger.Debug("Cancelling background work failed: " + ex.Message);
            }

            _timer.Stop();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _settingsForm?.Dispose();
            _historyForm?.Dispose();
            _window.Dispose();
            _client.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        ThemeManager.ApplyToMenu(_menu);
        if (_settingsForm is { IsDisposed: false })
        {
            ThemeManager.ApplyToForm(_settingsForm);
        }

        if (_historyForm is { IsDisposed: false })
        {
            ThemeManager.ApplyToForm(_historyForm);
        }
    }

    private void StartRefresh(bool userInitiated) => _ = RefreshAsync(userInitiated);

    private async Task RefreshAsync(bool userInitiated)
    {
        if (_busy)
        {
            Logger.Info("A refresh is already running, skipping this trigger.");
            return;
        }

        _busy = true;
        UpdateMenuState();
        try
        {
            Logger.Info("=== refresh cycle start (userInitiated=" + userInitiated + ") ===");
            List<BingImageInfo> images = await _client
                .FetchAsync(_config.Market, 0, BingClient.MaxImageCount, _shutdown.Token)
                .ConfigureAwait(true);

            _images = images;
            _historyForm?.OnImagesRefreshed(images);

            await ApplyIndexAsync(0, force: userInitiated).ConfigureAwait(true);

            WallpaperService.Cleanup(Paths.WallpaperDirectory, _config.KeepDays, _appliedPath);
            WallpaperService.RemoveStaleResolutions(Paths.WallpaperDirectory, _config.Resolution, _appliedPath);
            Logger.Info("=== refresh cycle done ===");
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Refresh cancelled (application is shutting down).");
        }
        catch (Exception ex)
        {
            Logger.Error("Refresh cycle failed.", ex);
            _titleItem.Text = "刷新失败，详见日志文件";
            if (userInitiated)
            {
                ErrorDialog.Show("刷新失败", Logger.Describe(ex));
            }
        }
        finally
        {
            _busy = false;
            UpdateMenuState();
        }
    }

    /// <summary>Moves inside the 8 day window: -1 goes newer, +1 goes older.</summary>
    private void MoveBy(int delta)
    {
        int target = _currentIndex + delta;
        if (target < 0 || target >= _images.Count)
        {
            return;
        }

        _ = MoveToAsync(target);
    }

    private async Task MoveToAsync(int index)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateMenuState();
        try
        {
            await ApplyIndexAsync(index, force: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            Logger.Info("Wallpaper switch cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Error("Could not switch the wallpaper.", ex);
            ErrorDialog.Show("切换壁纸失败", Logger.Describe(ex));
        }
        finally
        {
            _busy = false;
            UpdateMenuState();
        }
    }

    /// <summary>Applies a history selection (called by HistoryForm).</summary>
    public Task ApplyFromHistoryAsync(int index) => MoveToAsync(index);

    private bool IsCurrentWallpaper(string path)
    {
        string full = Path.GetFullPath(path);
        if (_appliedPath is not null
            && string.Equals(Path.GetFullPath(_appliedPath), full, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? registryValue = WallpaperService.GetCurrentWallpaperFromRegistry();
        return registryValue is not null
               && string.Equals(registryValue.Trim(), full, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateMenuState()
    {
        if (_appliedImage is not null)
        {
            _titleItem.Text = Truncate(_appliedImage.DisplayLine, 80);
            _tray.Text = Truncate("必应壁纸 · " + _appliedImage.DisplayTitle, 63);
        }
        else if (!_busy)
        {
            _titleItem.Text = _images.Count == 0 ? "尚未获取到壁纸信息" : _titleItem.Text;
        }

        if (_busy)
        {
            _titleItem.Text = "正在处理…";
        }

        // Clickable only when there is somewhere to go: no link, or a title that
        // currently says something else, means the row is just a caption.
        _titleItem.Enabled = !_busy && !string.IsNullOrWhiteSpace(_appliedImage?.CopyrightLink);

        _newerItem.Enabled = !_busy && _currentIndex > 0;
        _olderItem.Enabled = !_busy && _images.Count > 0 && _currentIndex < _images.Count - 1;
        _refreshItem.Enabled = !_busy;
        _historyItem.Enabled = !_busy;

        if (ThemeManager.IsDark)
        {
            // Disabled items need their colour refreshed after the enabled state changes.
            ThemeManager.ApplyToMenu(_menu);
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_config);
            _settingsForm.SettingsChanged += OnSettingsChanged;
        }

        ShowForm(_settingsForm);
    }

    private void ShowHistory()
    {
        if (_historyForm is null || _historyForm.IsDisposed)
        {
            _historyForm = new HistoryForm(this);
        }

        ShowForm(_historyForm);
        _historyForm.LoadImages(_images);
    }

    private static void ShowForm(Form form)
    {
        if (!form.Visible)
        {
            form.Show();
        }

        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.Activate();
        form.BringToFront();
        NativeMethods.SetForegroundWindow(form.Handle);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        Logger.Info("Setting changed: " + e.Kind);
        switch (e.Kind)
        {
            case SettingKind.Market:
            case SettingKind.Resolution:
                _appliedPath = null;
                _appliedImage = null;
                _currentIndex = 0;
                StartRefresh(userInitiated: true);
                break;

            case SettingKind.Fit:
                if (_appliedPath is not null)
                {
                    WallpaperService.Apply(_appliedPath, _config.Fit);
                }

                break;

            case SettingKind.Theme:
                ThemeManager.SetMode(_config.Theme);
                break;

            case SettingKind.Interval:
                _timer.Stop();
                _timer.Interval = GetIntervalMilliseconds();
                _timer.Start();
                Logger.Info("Refresh timer set to " + _config.RefreshIntervalHours + " hour(s).");
                break;

            case SettingKind.KeepDays:
                WallpaperService.Cleanup(Paths.WallpaperDirectory, _config.KeepDays, _appliedPath);
                break;

            case SettingKind.RunAtStartup:
                if (_config.RunAtStartup)
                {
                    AutoStartManager.Enable();
                }
                else
                {
                    AutoStartManager.Disable();
                }

                break;
        }
    }

    private void OpenWallpaperFolder()
    {
        try
        {
            Paths.EnsureWallpaperDirectory();
            Process.Start(new ProcessStartInfo(Paths.WallpaperDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open the wallpaper folder.", ex);
        }
    }

    private void OpenCopyrightLink()
    {
        string? link = _appliedImage?.CopyrightLink;
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            Logger.Info("Opened image source: " + link);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open the image source link.", ex);
        }
    }

    private void ExitApplication()
    {
        Logger.Info("Exit requested from the tray menu.");
        _tray.Visible = false;
        ExitThread();
    }

    private int GetIntervalMilliseconds()
    {
        int hours = AppConfig.Clamp(
            _config.RefreshIntervalHours,
            AppConfig.MinRefreshIntervalHours,
            AppConfig.MaxRefreshIntervalHours);
        return hours * 60 * 60 * 1000;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value.Substring(0, maxLength - 1) + "…";
}

/// <summary>
/// Invisible top level window. It exists for two reasons: it receives the
/// WM_SETTINGCHANGE broadcast used for live theme switching, and it gives the
/// context something to marshal calls onto.
/// </summary>
internal sealed class HiddenWindow : Form
{
    public HiddenWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-32000, -32000);
        Size = new Size(1, 1);
        Text = "BingWallpaper message window";

        // Force handle creation: broadcasts and BeginInvoke both need a real HWND.
        _ = Handle;
    }

    public event EventHandler? SystemColorSchemeChanged;

    protected override void SetVisibleCore(bool value) => base.SetVisibleCore(false);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_SETTINGCHANGE)
        {
            string? area = m.LParam != IntPtr.Zero ? Marshal.PtrToStringUni(m.LParam) : null;
            if (string.Equals(area, "ImmersiveColorSet", StringComparison.Ordinal))
            {
                Logger.Debug("WM_SETTINGCHANGE/ImmersiveColorSet received.");
                SystemColorSchemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        base.WndProc(ref m);
    }
}
