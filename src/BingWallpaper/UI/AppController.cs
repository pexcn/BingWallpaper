using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BingWallpaper.Theme;
using Microsoft.UI.Dispatching;

namespace BingWallpaper.UI;

/// <summary>
/// The application itself: a tray icon, a timer and the refresh logic.
///
/// There is no main window. WinUI 3 would normally end the process when the last
/// window closes, which is why <see cref="App"/> switches the dispatcher to
/// explicit shutdown - the settings and history windows come and go, the tray
/// entry is what stays.
/// </summary>
internal sealed class AppController : IDisposable
{
    private readonly AppConfig _config;
    private readonly BingClient _client = new BingClient();
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();

    private TrayIcon? _tray;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;

    private List<BingImageInfo> _images = new List<BingImageInfo>();
    private int _currentIndex;
    private string? _appliedPath;
    private BingImageInfo? _appliedImage;
    private string _title = "正在获取今日壁纸…";
    private bool _busy;
    private bool _disposed;

    public AppController(AppConfig config, DispatcherQueue dispatcher)
    {
        _config = config;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = GetInterval();
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => StartRefresh(userInitiated: false);
    }

    /// <summary>Raised when the user picked "退出" in the tray menu.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Metadata of the last 8 days, newest first.</summary>
    public IReadOnlyList<BingImageInfo> Images => _images;

    public AppConfig Config => _config;

    public BingClient Client => _client;

    /// <summary>Index into <see cref="Images"/>, or -1 when the wallpaper is not in that list.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>Whether the wallpaper is held against the refresh timer.</summary>
    public bool IsPinned => _config.IsPinned;

    public CancellationToken ShutdownToken => _shutdown.Token;

    /// <summary>Puts the tray icon up and starts the first check.</summary>
    public void Start()
    {
        _tray = new TrayIcon(OnTrayCommand);
        _tray.SystemColorSchemeChanged += (_, _) => ThemeManager.HandleSystemThemeChanged();
        _tray.ApplyTheme(ThemeManager.IsDark);
        ThemeManager.ThemeChanged += OnThemeChanged;

        _timer.Start();

        // Before the first network call: from here on the cleanup passes and the
        // menu have something to work with even while the metadata request is still
        // in flight.
        RestorePinnedWallpaper();
        UpdateTray();

        StartRefresh(userInitiated: false);
    }

    /// <summary>Called from the single instance listener when a second copy was started.</summary>
    public void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_config, OnSettingChanged);
            _settingsWindow.Hidden += (_, _) => Logger.Debug("Settings window hidden.");
        }

        _settingsWindow.ShowAndActivate();
    }

    public void ShowHistory()
    {
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(this);
        }

        _historyWindow.ShowAndActivate();
        _historyWindow.LoadImages(_images);
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
        UpdateTray();
    }

    /// <summary>
    /// Applies a history selection. Picking a picture out of the window is a
    /// deliberate choice, so it pins on its own - unlike stepping through the list
    /// from the tray menu, which is just browsing.
    /// </summary>
    public Task ApplyFromHistoryAsync(int index) => MoveToAsync(index, pinAfterwards: true);

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
        string path = Path.Combine(Paths.WallpaperDirectory, image.GetFileName(_config.Resolution));

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
        UpdateTray();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
        _tray?.Dispose();
        _tray = null;
        _settingsWindow?.CloseForGood();
        _settingsWindow = null;
        _historyWindow?.CloseForGood();
        _historyWindow = null;
        _client.Dispose();
        _shutdown.Dispose();
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.OpenSource:
                OpenCopyrightLink();
                break;

            case TrayCommand.Older:
                MoveBy(1);
                break;

            case TrayCommand.Newer:
                MoveBy(-1);
                break;

            case TrayCommand.History:
                ShowHistory();
                break;

            case TrayCommand.Refresh:
                StartRefresh(userInitiated: true);
                break;

            case TrayCommand.Pin:
                TogglePin();
                break;

            case TrayCommand.Folder:
                OpenWallpaperFolder();
                break;

            case TrayCommand.Settings:
                ShowSettings();
                break;

            case TrayCommand.Exit:
                Logger.Info("Exit requested from the tray menu.");
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _tray?.ApplyTheme(ThemeManager.IsDark);
        _settingsWindow?.ApplyTheme();
        _historyWindow?.ApplyTheme();
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
        UpdateTray();
        try
        {
            Logger.Info("=== refresh cycle start (userInitiated=" + userInitiated + ") ===");
            List<BingImageInfo> images = await _client
                .FetchAsync(_config.Market, 0, BingClient.MaxImageCount, _shutdown.Token)
                .ConfigureAwait(true);

            _images = images;
            _historyWindow?.OnImagesRefreshed(images);

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
            _title = "刷新失败，详见日志文件";
            if (userInitiated)
            {
                ErrorWindow.Show("刷新失败", Logger.Describe(ex));
            }
        }
        finally
        {
            _busy = false;
            UpdateTray();
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
        UpdateTray();
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
            ErrorWindow.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        Logger.Info(value.Length == 0 ? "Wallpaper pin released." : "Wallpaper pinned to " + value + ".");
        UpdateTray();
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
        UpdateTray();
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
            ErrorWindow.Show("切换壁纸失败", Logger.Describe(ex));
        }
        finally
        {
            _busy = false;
            UpdateTray();
        }
    }

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

    /// <summary>Recomputes what the tray menu shows and hands it over.</summary>
    private void UpdateTray()
    {
        bool pinned = _config.IsPinned;
        string prefix = pinned ? "必应壁纸（已固定） · " : "必应壁纸 · ";
        string tooltip = "必应壁纸";

        if (_appliedImage is not null)
        {
            _title = _appliedImage.DisplayLine;
            tooltip = prefix + _appliedImage.DisplayTitle;
        }
        else if (pinned && _appliedPath is not null)
        {
            // Pinned long enough to have left the eight day window: the file name is
            // all the metadata there is.
            string label = DescribeWallpaperFile(_config.PinnedWallpaper);
            _title = label;
            tooltip = prefix + label;
        }
        else if (!_busy && _images.Count == 0)
        {
            _title = "尚未获取到壁纸信息";
        }

        TrayMenuState state = new TrayMenuState
        {
            Title = _busy ? "正在处理…" : _title,
            Tooltip = _busy ? "必应壁纸 · 正在处理…" : tooltip,
            SourceEnabled = !string.IsNullOrWhiteSpace(_appliedImage?.CopyrightLink),

            // _currentIndex == -1 means the wallpaper is not in the list at all: there
            // is a newer picture to go to, but nothing older.
            NewerEnabled = _images.Count > 0 && _currentIndex != 0,
            OlderEnabled = _currentIndex >= 0 && _currentIndex < _images.Count - 1,
            Pinned = pinned,
            PinEnabled = pinned || _appliedPath is not null,
            Busy = _busy,
        };

        _tray?.Update(state);

        // The picker paints the same state on a tile, and it can be open while this
        // runs - stepping through the list from the tray menu moves both badges.
        _historyWindow?.RefreshCurrentMarker();
    }

    private void OnSettingChanged(SettingKind kind)
    {
        Logger.Info("Setting changed: " + kind);
        switch (kind)
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
                _timer.Interval = GetInterval();
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

    private TimeSpan GetInterval() => TimeSpan.FromHours(
        Math.Clamp(
            _config.RefreshIntervalHours,
            AppConfig.MinRefreshIntervalHours,
            AppConfig.MaxRefreshIntervalHours));

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
}
