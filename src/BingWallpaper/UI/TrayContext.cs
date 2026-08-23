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
    private readonly ToolStripMenuItem _pinItem;
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
        _pinItem = new ToolStripMenuItem("固定当前壁纸", null, (_, _) => TogglePin())
        {
            Enabled = false,
            ToolTipText = "固定后不再随检查间隔自动更换",
        };
        _folderItem = new ToolStripMenuItem("打开壁纸目录", null, (_, _) => OpenWallpaperFolder());
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
            _pinItem,
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

        // Before the first network call: from here on the cleanup passes and the
        // menu have something to work with even while the metadata request is still
        // in flight.
        RestorePinnedWallpaper();

        // Run the first check as soon as the message loop starts.
        _window.BeginInvoke(new Action(() => StartRefresh(userInitiated: false)));
    }

    /// <summary>Metadata of the last 8 days, newest first.</summary>
    public IReadOnlyList<BingImageInfo> Images => _images;

    public AppConfig Config => _config;

    public BingClient Client => _client;

    /// <summary>Index into <see cref="Images"/>, or -1 when the wallpaper is not in that list.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Whether the wallpaper is held against the refresh timer.</summary>
    public bool IsPinned => _config.IsPinned;

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

            if (_config.IsPinned)
            {
                await EnsurePinnedAsync().ConfigureAwait(true);
            }
            else
            {
                await ApplyIndexAsync(0, force: userInitiated).ConfigureAwait(true);
            }

            WallpaperService.Cleanup(Paths.WallpaperDirectory, _config.KeepDays, BuildProtectedFiles());
            WallpaperService.RemoveStaleResolutions(
                Paths.WallpaperDirectory,
                _config.Resolution,
                BuildProtectedFiles());
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

    /// <summary>
    /// Puts a pinned wallpaper back on the desktop at startup, using nothing but the
    /// local file - no network, so the pin is honoured before the first request is
    /// even sent. This is the one place that can repair a pin: whatever changed the
    /// wallpaper while the program was not running (another tool, a theme, a system
    /// reset) is undone here, and the desktop is left alone from then on.
    /// </summary>
    private void RestorePinnedWallpaper()
    {
        if (!_config.IsPinned)
        {
            return;
        }

        string path = Paths.ResolveWallpaperFile(_config.PinnedWallpaper);
        if (!File.Exists(path))
        {
            // It may still be downloadable; EnsurePinnedAsync decides once the
            // metadata is in.
            Logger.Warn("The pinned wallpaper is not in the cache: " + _config.PinnedWallpaper);
            _currentIndex = -1;
            UpdateMenuState();
            return;
        }

        // Unconditionally, without asking what is on the desktop right now: the only
        // way to ask is the registry value, and that answer is not reliable enough to
        // skip on (see IsCurrentWallpaper). One SystemParametersInfoW call per start
        // is cheap, and applying a picture that is already there changes nothing.
        Logger.Info("Restoring the pinned wallpaper: " + _config.PinnedWallpaper);
        WallpaperService.Apply(path, _config.Fit);

        _appliedPath = path;
        _currentIndex = -1;
        UpdateMenuState();
    }

    /// <summary>
    /// Reconciles the pin with the metadata that was just fetched, without touching
    /// the desktop unless it has to. Three cases: the picture is still inside the
    /// eight day window and keeps its title; it has aged out and lives on as a file
    /// with no metadata left; or the file is gone and has to be fetched again - or
    /// given up on, when it is out of the window as well.
    /// </summary>
    private async Task EnsurePinnedAsync()
    {
        string fileName = _config.PinnedWallpaper;
        int index = FindImageIndex(fileName);
        string path = Paths.ResolveWallpaperFile(fileName);

        if (!File.Exists(path))
        {
            if (index < 0)
            {
                Logger.Warn("The pinned wallpaper is gone and out of reach, releasing the pin: " + fileName);
                SetPinned(null);
                await ApplyIndexAsync(0, force: true).ConfigureAwait(true);
                return;
            }

            Logger.Info("The pinned wallpaper is missing, downloading it again: " + fileName);
            await ApplyIndexAsync(index, force: true).ConfigureAwait(true);
            return;
        }

        // The file is there and the desktop was not touched by anyone this program
        // knows about, so there is nothing to apply - only the metadata to catch up.
        _appliedPath = path;
        _currentIndex = index;
        _appliedImage = index >= 0 ? _images[index] : null;
        Logger.Info("Wallpaper is pinned to " + fileName + ", leaving the desktop alone.");
        UpdateMenuState();
    }

    /// <summary>Index of the image whose cache file is <paramref name="fileName"/>, or -1.</summary>
    private int FindImageIndex(string fileName)
    {
        for (int i = 0; i < _images.Count; i++)
        {
            if (string.Equals(
                    _images[i].GetFileName(_config.Resolution),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The files the cleanup passes must leave alone: the wallpaper on the desktop
    /// and the pinned one. Usually the same file - but not while a pinned picture is
    /// being restored or downloaded again, which is exactly when losing it would hurt.
    /// </summary>
    private List<string> BuildProtectedFiles()
    {
        List<string> files = new List<string>(2);
        if (_appliedPath is not null)
        {
            files.Add(_appliedPath);
        }

        if (_config.IsPinned)
        {
            files.Add(Paths.ResolveWallpaperFile(_config.PinnedWallpaper));
        }

        return files;
    }

    /// <summary>
    /// The only writer of the pin; null releases it. The value in memory changes
    /// only once it is on disk, so a failed save leaves the program and the
    /// configuration file saying the same thing.
    /// </summary>
    private void SetPinned(string? fileName)
    {
        string value = fileName ?? string.Empty;
        if (string.Equals(_config.PinnedWallpaper, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string previous = _config.PinnedWallpaper;
        _config.PinnedWallpaper = value;
        try
        {
            _config.Save(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            _config.PinnedWallpaper = previous;
            Logger.Error("Could not save the pinned wallpaper.", ex);
            ErrorDialog.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        Logger.Info(value.Length == 0 ? "Wallpaper pin released." : "Wallpaper pinned to " + value + ".");
        UpdateMenuState();
    }

    private void TogglePin()
    {
        if (_config.IsPinned)
        {
            SetPinned(null);
            if (_config.IsPinned)
            {
                // The save failed, nothing was released.
                return;
            }

            // Back under the timer: restart it so the first automatic change is a
            // full interval away, and go to today's picture now rather than at some
            // arbitrary point within the hour.
            _timer.Stop();
            _timer.Start();
            StartRefresh(userInitiated: true);
            return;
        }

        if (_appliedPath is null)
        {
            return;
        }

        SetPinned(Path.GetFileName(_appliedPath));
    }

    /// <summary>Moves inside the 8 day window: -1 goes newer, +1 goes older.</summary>
    private void MoveBy(int delta)
    {
        int target;
        if (_currentIndex >= 0)
        {
            target = _currentIndex + delta;
        }
        else if (delta < 0)
        {
            // A pinned picture that has aged out of the window sits before the oldest
            // entry, so the only way back into the list is towards the newer end.
            target = _images.Count - 1;
        }
        else
        {
            return;
        }

        if (target < 0 || target >= _images.Count)
        {
            return;
        }

        // Stepping through the list decides nothing: it carries a pin that is already
        // set, and never creates one.
        _ = MoveToAsync(target, pinAfterwards: _config.IsPinned);
    }

    private async Task MoveToAsync(int index, bool pinAfterwards)
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
            if (pinAfterwards && _appliedPath is not null)
            {
                SetPinned(Path.GetFileName(_appliedPath));
            }
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

    /// <summary>
    /// Applies a history selection (called by HistoryForm). Picking a picture out of
    /// the window is a deliberate choice, so it pins on its own - unlike stepping
    /// through the list from the tray menu, which is just browsing.
    /// </summary>
    public Task ApplyFromHistoryAsync(int index) => MoveToAsync(index, pinAfterwards: true);

    /// <summary>
    /// Whether <paramref name="path"/> is, as far as this program can tell, already
    /// the wallpaper - used to skip an apply that would change nothing.
    /// <para>
    /// Only ever a reason to do less work, never a statement of fact. It answers from
    /// <see cref="_appliedPath"/> first, so it cannot notice a wallpaper someone else
    /// changed while the program was running; and the registry value it falls back to
    /// is what Windows chose to record, which is not guaranteed to be the path that
    /// was handed to SystemParametersInfoW. Code that has to *make* a picture the
    /// wallpaper must apply it rather than ask this first.
    /// </para>
    /// </summary>
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
        bool pinned = _config.IsPinned;
        string prefix = pinned ? "必应壁纸（已固定） · " : "必应壁纸 · ";

        if (_appliedImage is not null)
        {
            _titleItem.Text = Truncate(_appliedImage.DisplayLine, 80);
            _tray.Text = Truncate(prefix + _appliedImage.DisplayTitle, 63);
        }
        else if (pinned && _appliedPath is not null)
        {
            // Pinned long enough to have left the eight day window: the file name is
            // all the metadata there is.
            string label = DescribeWallpaperFile(_config.PinnedWallpaper);
            _titleItem.Text = label;
            _tray.Text = Truncate(prefix + label, 63);
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

        // _currentIndex == -1 means the wallpaper is not in the list at all: there is
        // a newer picture to go to, but nothing older.
        _newerItem.Enabled = !_busy && _images.Count > 0 && _currentIndex != 0;
        _olderItem.Enabled = !_busy && _currentIndex >= 0 && _currentIndex < _images.Count - 1;
        _refreshItem.Enabled = !_busy;
        _historyItem.Enabled = !_busy;

        _pinItem.Checked = pinned;
        _pinItem.Enabled = !_busy && (pinned || _appliedPath is not null);

        // The picker paints the same state on a tile, and it can be open while this
        // runs - stepping through the list from the tray menu moves both badges.
        _historyForm?.RefreshCurrentMarker();

        // Every Enabled flag above was just recomputed, and the item colours follow
        // them. Not ApplyToMenu: that also builds a renderer, which nothing here needs.
        ThemeManager.RefreshMenuItemColors(_menu);
    }

    private void ShowSettings()
    {
        // Closing a window disposes it, so this is the usual path rather than a corner
        // case: every open builds a fresh window, which is what gets it centred and
        // free of whatever state the last one was left in. The field is kept only so
        // that a window already on screen is brought forward instead of duplicated.
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
                // The pinned file belongs to the old settings, so keeping it would
                // contradict the change that was just made.
                SetPinned(null);
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
                WallpaperService.Cleanup(Paths.WallpaperDirectory, _config.KeepDays, BuildProtectedFiles());
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

    /// <summary>
    /// Menu caption for a picture whose metadata is out of reach: the file name
    /// still carries the date it was published on.
    /// </summary>
    private static string DescribeWallpaperFile(string fileName)
    {
        if (BingImageInfo.TryParseFileName(fileName, out string startDate, out _))
        {
            return BingImageInfo.FormatDate(startDate) + " 的壁纸";
        }

        return Path.GetFileNameWithoutExtension(fileName);
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
