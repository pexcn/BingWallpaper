using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
    private readonly ThumbnailCache _thumbnails;
    private readonly HiddenWindow _window;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    // Replaced after every right click, see RecreateMenu.
    private ContextMenu _menu;

    private readonly MenuItem _titleItem;
    private readonly MenuItem _newerItem;
    private readonly MenuItem _olderItem;
    private readonly MenuItem _pickerItem;
    private readonly MenuItem _refreshItem;
    private readonly MenuItem _pinItem;
    private readonly MenuItem _folderItem;
    private readonly MenuItem _settingsItem;
    private readonly MenuItem _exitItem;

    private readonly CancellationTokenSource _shutdown = new();

    private List<BingImageInfo> _images = new();
    private int _currentIndex;
    private string? _appliedPath;
    private BingImageInfo? _appliedImage;
    private SettingsForm? _settingsForm;
    private PickerForm? _pickerForm;
    private bool _busy;

    /// <summary>
    /// Which list the two stepping rows walk: favorites\ when set, the 8 day window
    /// when not.
    ///
    /// <para>
    /// It has to be remembered rather than worked out, because the two lists overlap.
    /// Favouriting moves the file but leaves the metadata in <see cref="_images"/>, so
    /// a picture from three days ago is in both, and asking the folder would answer
    /// "favourites" for a tile the user clicked on the recent tab. Where the click was
    /// is the only thing that knows.
    /// </para>
    /// <para>
    /// Chosen in two places, which are the two ways a wallpaper is picked:
    /// <see cref="ApplyFavorite"/> raises it, and <see cref="ApplyIndexAsync"/> -
    /// everything applied out of <see cref="_images"/> - clears it. The rest only
    /// clear it when its premise is gone: the pin released, the picture un-favourited,
    /// or a restart, which is where it starts life as a guess (see
    /// <see cref="RestorePinnedWallpaper"/>) because it is deliberately not persisted.
    /// </para>
    /// </summary>
    private bool _steppingFavorites;

    // File name the two below were read for; empty when nothing is cached.
    private string _pinnedMetadataFor = string.Empty;
    private string? _pinnedTitle;
    private string? _pinnedLink;

    /// <summary>
    /// Set when a trigger arrives while a refresh is running. These used to be dropped,
    /// which left the INI naming one setting while the desktop showed a picture from
    /// another, with nothing to reconcile the two.
    ///
    /// <para>
    /// SettingsForm debounces its drop downs, so the bursts this was written for no
    /// longer reach here - but it is not only about bursts. Any two triggers close
    /// enough together still collide: changing the resolution and then the market goes
    /// through different commit paths and cannot be debounced into one, and a timer
    /// tick lands whenever it lands.
    /// </para>
    ///
    /// <para>
    /// One flag rather than a queue, because a queue would have nothing useful in it:
    /// the next pass reads <see cref="AppConfig.Market"/> again, so a single rerun ends
    /// on whatever the settings settled on no matter how many triggers it collapsed.
    /// </para>
    /// </summary>
    private bool _rerunRequested;
    private bool _rerunUserInitiated;

    private bool _disposed;

    public TrayContext(AppConfig config)
    {
        _config = config;
        _thumbnails = new ThumbnailCache(_client);

        _window = new HiddenWindow();
        _window.SystemColorSchemeChanged += (_, _) => ThemeManager.HandleSystemThemeChanged();

        // The title row doubles as the "open the image source" command. It has to be
        // enabled to raise Click at all, so it only greys out - and reads as a plain
        // header - while there is no link behind it.
        _titleItem = new MenuItem("正在获取今日壁纸…", (_, _) => OpenCopyrightLink()) { Enabled = false };
        _newerItem = new MenuItem("下一张", (_, _) => MoveBy(-1)) { Enabled = false };
        _olderItem = new MenuItem("上一张", (_, _) => MoveBy(1)) { Enabled = false };
        _pickerItem = new MenuItem("选择壁纸...", (_, _) => ShowPicker());
        _refreshItem = new MenuItem("立即刷新", (_, _) => StartRefresh(userInitiated: true));
        _pinItem = new MenuItem("锁定当前壁纸", (_, _) => TogglePin()) { Enabled = false };
        _folderItem = new MenuItem("打开壁纸目录", (_, _) => OpenWallpaperFolder());
        _settingsItem = new MenuItem("设置...", (_, _) => ShowSettings());
        _exitItem = new MenuItem("退出", (_, _) => ExitApplication());

        _menu = BuildMenu();

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Tray,
            Text = "必应壁纸",
            Visible = true,
            ContextMenu = _menu,
        };
        _tray.DoubleClick += (_, _) => ShowPicker();

        // NotifyIcon shows the menu from WM_RBUTTONUP and only raises MouseUp once
        // TrackPopupMenuEx has returned, so this runs with the menu already closed.
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                RecreateMenu();
            }
        };

        _timer = new System.Windows.Forms.Timer { Interval = GetIntervalMilliseconds() };
        _timer.Tick += (_, _) => StartRefresh(userInitiated: false);
        _timer.Start();

        ThemeManager.ThemeChanged += OnThemeChanged;

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

    /// <summary>Thumbnails of <see cref="Images"/>, kept across picker windows.</summary>
    public ThumbnailCache Thumbnails => _thumbnails;

    /// <summary>Index into <see cref="Images"/>, or -1 when the wallpaper is not in that list.</summary>
    public int CurrentIndex => _currentIndex;

    /// <summary>
    /// File name of the wallpaper this program last applied, or null. The picker
    /// badges a tile by it: the index into <see cref="Images"/> cannot answer for a
    /// favourite that left the eight day window years ago.
    /// </summary>
    public string? AppliedFileName => _appliedPath is null ? null : Path.GetFileName(_appliedPath);

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
            Logger.Warn("settings: activating the window failed error=" + ex.Message);
        }
    }

    /// <summary>
    /// Shares metadata that the picker window fetched on its own, so both windows
    /// index into the same list.
    /// </summary>
    public void AdoptImages(List<BingImageInfo> images)
    {
        if (images.Count == 0)
        {
            return;
        }

        SetImages(images);
        UpdateMenuState();
    }

    /// <summary>
    /// The one place the current list is replaced. The thumbnail cache is trimmed to
    /// it here rather than at each call site, so it cannot start collecting eight more
    /// entries a day the moment someone adds a third way to set the list.
    /// </summary>
    private void SetImages(List<BingImageInfo> images)
    {
        _images = images;
        _thumbnails.Retain(images);
    }

    /// <summary>Downloads (if needed) and applies the image at <paramref name="index"/>.</summary>
    public async Task ApplyIndexAsync(int index, bool force)
    {
        if (index < 0 || index >= _images.Count)
        {
            Logger.Warn("apply: index out of range index=" + index + " count=" + _images.Count);
            return;
        }

        BingImageInfo image = _images[index];
        Paths.EnsureWallpaperDirectory();

        // Through the resolver: a favourited picture lives one folder down, and
        // looking only in the daily cache would download a second copy of a file that
        // is already on disk.
        string path = Paths.ResolveWallpaperFile(image.GetFileName(_config.Resolution));

        bool cached = File.Exists(path) && new FileInfo(path).Length > 0;
        if (cached)
        {
            Logger.Debug("apply: cache hit path=" + path);
        }
        else
        {
            // A download always lands in the daily cache, even when the resolver named
            // favorites\ - it only did so for a file that turned out to be missing or
            // empty. Exactly three operations write into favorites\ (a picture moved
            // in, one of ours moved out, favorites.txt replaced), and downloading is
            // not one of them: it writes a .tmp and renames over the target, which is
            // a code path that can delete a picture in there.
            path = Path.Combine(Paths.WallpaperDirectory, image.GetFileName(_config.Resolution));
            await _client
                .DownloadImageAsync(image.GetImageUrl(_config.Resolution), path, _shutdown.Token)
                .ConfigureAwait(true);
        }

        if (!force && cached && IsCurrentWallpaper(path))
        {
            Logger.Info("apply: already up to date path=" + path);
        }
        else
        {
            WallpaperService.Apply(path, _config.Fit);
        }

        _currentIndex = index;
        _appliedPath = path;
        _appliedImage = image;

        // Everything that lands here came out of _images - a refresh, a step through
        // the window, a tile on the recent tab - so this is where stepping goes back
        // to the window, whether or not the file happens to sit in favorites\.
        _steppingFavorites = false;
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
                Logger.Debug("shutdown: cancelling background work failed error=" + ex.Message);
            }

            _timer.Stop();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            _settingsForm?.Dispose();
            _pickerForm?.Dispose();
            _window.Dispose();
            _client.Dispose();
            _shutdown.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        // Nothing to do for the tray menu: Windows themes the native popup itself.
        if (_settingsForm is { IsDisposed: false })
        {
            ThemeManager.ApplyToForm(_settingsForm);
        }

        if (_pickerForm is { IsDisposed: false })
        {
            ThemeManager.ApplyToForm(_pickerForm);
        }
    }

    /// <summary>
    /// Asks for a refresh. A trigger that arrives while one is running is remembered
    /// rather than dropped, and acted on once the running one is done.
    /// </summary>
    private void StartRefresh(bool userInitiated)
    {
        if (_busy)
        {
            Logger.Info("refresh: already running, queued one more pass");
            _rerunRequested = true;

            // Kept if any of the collapsed triggers was the user's: it decides whether
            // a failure is reported in a dialog, and losing that to a timer tick would
            // silence an error someone is waiting to hear about.
            _rerunUserInitiated |= userInitiated;
            return;
        }

        _ = RefreshAsync(userInitiated);
    }

    private async Task RefreshAsync(bool userInitiated)
    {
        // A loop rather than a recursive call at the end: a burst of triggers chains one
        // pass after another, and recursion would leave every one of their state machines
        // alive on the heap until the innermost returns.
        while (true)
        {
            await RunRefreshPassAsync(userInitiated).ConfigureAwait(true);

            if (!_rerunRequested || _disposed || _shutdown.IsCancellationRequested)
            {
                return;
            }

            _rerunRequested = false;
            userInitiated = _rerunUserInitiated;
            _rerunUserInitiated = false;
            Logger.Info("refresh: running the queued pass");
        }
    }

    private async Task RunRefreshPassAsync(bool userInitiated)
    {
        _busy = true;
        UpdateMenuState();
        try
        {
            Logger.Info("refresh: start userinitiated=" + userInitiated);
            List<BingImageInfo> images = await _client
                .FetchAsync(_config.Market, 0, BingClient.MaxImageCount, _shutdown.Token)
                .ConfigureAwait(true);

            SetImages(images);
            _pickerForm?.OnImagesRefreshed(images);

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
            Logger.Info("refresh: done");
        }
        catch (OperationCanceledException)
        {
            Logger.Info("refresh: cancelled, application is shutting down");
        }
        catch (Exception ex)
        {
            Logger.Error("refresh: cycle failed", ex);
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

        // Which list to step through was decided by a click in a session that is over,
        // and it is deliberately not written to the INI: the folder is the only clue
        // left here, and a good enough one. The case it cannot tell apart - a
        // favourite that is also still in the eight day window - needs the pin to be
        // younger than eight days, while a lock that survived a restart has usually
        // long left it.
        _steppingFavorites = Favorites.Contains(_config.PinnedWallpaper);

        string path = Paths.ResolveWallpaperFile(_config.PinnedWallpaper);
        if (!File.Exists(path))
        {
            // It may still be downloadable; EnsurePinnedAsync decides once the
            // metadata is in.
            Logger.Warn("pin: not in the cache file=" + _config.PinnedWallpaper);
            _currentIndex = -1;
            UpdateMenuState();
            return;
        }

        // Unconditionally, without asking what is on the desktop right now: the only
        // way to ask is the registry value, and that answer is not reliable enough to
        // skip on (see IsCurrentWallpaper). One SystemParametersInfoW call per start
        // is cheap, and applying a picture that is already there changes nothing.
        Logger.Info("pin: restoring file=" + _config.PinnedWallpaper);
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
                Logger.Warn("pin: file gone and not downloadable, releasing the pin file=" + fileName);
                SetPinned(null);
                await ApplyIndexAsync(0, force: true).ConfigureAwait(true);
                return;
            }

            Logger.Info("pin: file missing, downloading again file=" + fileName);
            await ApplyIndexAsync(index, force: true).ConfigureAwait(true);
            return;
        }

        // The file is there and the desktop was not touched by anyone this program
        // knows about, so there is nothing to apply - only the metadata to catch up.
        _appliedPath = path;
        _currentIndex = index;
        _appliedImage = index >= 0 ? _images[index] : null;
        Logger.Info("pin: active, desktop left alone file=" + fileName);
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
            Logger.Error("pin: saving the configuration failed", ex);
            ErrorDialog.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        if (value.Length == 0)
        {
            // Released, so the wallpaper is back under the timer - and the timer's
            // list is the window. Cleared here rather than left to the apply that
            // follows, because UpdateMenuState below would otherwise draw one menu
            // against a folder the pin no longer names.
            _steppingFavorites = false;
        }

        Logger.Info(value.Length == 0 ? "pin: released" : "pin: set file=" + value);
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

            if (HasTodaysMetadata())
            {
                // The list already names today's picture, so fetching it again could
                // only return the same entry. Applying it straight from the cache
                // keeps releasing a pin off the network entirely. Skipping the
                // refresh also skips its cleanup pass, which is what would drop the
                // file that just lost its protection - the next cycle does that.
                _ = MoveToAsync(0, pinAfterwards: false);
                return;
            }

            StartRefresh(userInitiated: true);
            return;
        }

        if (_appliedPath is null)
        {
            return;
        }

        SetPinned(Path.GetFileName(_appliedPath));
    }

    /// <summary>
    /// Whether the newest entry in <see cref="_images"/> is dated today, meaning a
    /// metadata request cannot turn up anything newer. Compared in local time because
    /// that is the clock the timer runs on; a market that has already rolled over (or
    /// not yet) merely fails the test and costs a request nobody notices.
    /// </summary>
    private bool HasTodaysMetadata()
        => _images.Count > 0
           && string.Equals(
               _images[0].StartDate,
               DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
               StringComparison.Ordinal);

    /// <summary>
    /// Whether the two menu rows step through favorites\ instead of the 8 day window.
    ///
    /// <para>
    /// <see cref="_steppingFavorites"/> is the answer; the folder is asked only to
    /// confirm it. A picture can leave favorites\ without this program applying
    /// anything - Explorer, or un-favouriting it from the picker - and the order the
    /// rows would step through then no longer exists, so the click has to fall back
    /// to the window rather than walk a folder the file is not in.
    /// </para>
    /// </summary>
    private bool InFavoriteMode => _steppingFavorites && Favorites.Contains(_config.PinnedWallpaper);

    /// <summary>
    /// Moves one picture: -1 goes newer, +1 goes older. Which list is stepped is
    /// <see cref="InFavoriteMode"/>'s answer; both are ordered newest first, so one
    /// delta means one direction in either.
    /// </summary>
    private void MoveBy(int delta)
    {
        if (InFavoriteMode && MoveWithinFavorites(delta))
        {
            return;
        }

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

    /// <summary>
    /// Steps through favorites\, and reports whether it was able to.
    ///
    /// <para>
    /// The folder is enumerated on every click rather than kept in a field. It is the
    /// only state there is - Explorer, the picker and this menu all write to it - so a
    /// field here would need invalidating from three directions to save a directory
    /// read that the apply behind it dwarfs.
    /// </para>
    /// <para>
    /// Nothing here downloads: a favourite is on disk by definition, so this goes
    /// through the same synchronous path the picker uses and never raises _busy.
    /// </para>
    /// </summary>
    private bool MoveWithinFavorites(int delta)
    {
        if (_busy)
        {
            // The rows are greyed while busy, but the menu was measured before the
            // refresh started and the click can still land. Dropped the way
            // MoveToAsync drops it, and reported as handled either way: the 8 day
            // path would only reach the same guard.
            return true;
        }

        string current = _config.PinnedWallpaper;
        List<FavoriteItem> items = Favorites.Scan();
        int index = -1;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].FileName, current, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            // InFavoriteMode saw the file a moment ago, so it left between the two
            // calls. Handing the click back to the 8 day window beats doing nothing.
            Logger.Warn("switch: the pinned favourite is gone file=" + current);
            return false;
        }

        int target = index + delta;
        if (target < 0 || target >= items.Count)
        {
            // An end of the folder is found by clicking, not by a greyed out row - see
            // UpdateMenuState. Still reported as handled: falling through to the 8 day
            // window here would jump out of the folder the user is walking.
            Logger.Debug("switch: no neighbour in the favourites index=" + index + " delta=" + delta);
            return true;
        }

        // The call the picker makes, pin included: stepping carries the lock along
        // instead of dropping the wallpaper back under the refresh timer.
        if (!ApplyFavorite(items[target].FileName))
        {
            ErrorDialog.Show("切换壁纸失败", "详见日志文件。");
        }

        return true;
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
            Logger.Info("switch: cancelled");
        }
        catch (Exception ex)
        {
            Logger.Error("switch: failed", ex);
            ErrorDialog.Show("切换壁纸失败", Logger.Describe(ex));
        }
        finally
        {
            _busy = false;
            UpdateMenuState();
        }
    }

    /// <summary>
    /// Applies a selection made in the picker. Picking a picture out of
    /// the window is a deliberate choice, so it pins on its own - unlike stepping
    /// through the list from the tray menu, which is just browsing.
    /// </summary>
    public Task ApplyFromPickerAsync(int index) => MoveToAsync(index, pinAfterwards: true);

    /// <summary>
    /// Makes sure a picture is in the local cache, downloading it when it is not.
    /// Favouriting a day nobody applied yet is the one path that needs the file
    /// without wanting it on the desktop.
    /// </summary>
    public async Task<string> EnsureCachedAsync(BingImageInfo image)
    {
        Paths.EnsureWallpaperDirectory();
        string fileName = image.GetFileName(_config.Resolution);
        string path = Paths.ResolveWallpaperFile(fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            return path;
        }

        // Into the daily cache, never into favorites\ - see ApplyIndexAsync.
        path = Path.Combine(Paths.WallpaperDirectory, fileName);
        await _client
            .DownloadImageAsync(image.GetImageUrl(_config.Resolution), path, _shutdown.Token)
            .ConfigureAwait(true);
        return path;
    }

    /// <summary>
    /// Applies a favourite by file name and pins it (called by the picker).
    ///
    /// <para>
    /// Pinned rather than merely applied, and for a stronger reason than picking a day
    /// out of the eight day window: a favourite is usually *outside* that window, so
    /// without the pin the next refresh would put today's picture back an hour later
    /// and the choice would look like it had been ignored.
    /// </para>
    /// </summary>
    public bool ApplyFavorite(string fileName)
    {
        string path = Paths.ResolveWallpaperFile(fileName);
        if (!File.Exists(path))
        {
            Logger.Warn("apply: the favourite is gone file=" + fileName);
            return false;
        }

        if (!WallpaperService.Apply(path, _config.Fit))
        {
            return false;
        }

        _appliedPath = path;
        _currentIndex = FindImageIndex(fileName);
        _appliedImage = _currentIndex >= 0 ? _images[_currentIndex] : null;

        // The one place stepping switches to the folder, and note it is set even when
        // FindImageIndex found the picture in the window as well: the click was on the
        // favourites tab, and that is the whole question.
        _steppingFavorites = true;
        SetPinned(fileName);
        UpdateMenuState();
        return true;
    }

    /// <summary>
    /// Called after a picture moved between wallpapers\ and favorites\.
    ///
    /// <para>
    /// The pin needs nothing here - it stores a bare file name precisely so that
    /// favouriting cannot break it. What does need saying is the path: this program
    /// keeps one, and HKCU\Control Panel\Desktop\Wallpaper holds another that now
    /// names a file which no longer exists. Windows itself has already transcoded the
    /// picture, so nothing on screen changes; the record is what is being repaired.
    /// </para>
    /// </summary>
    public void NotifyWallpaperMoved(string fileName)
    {
        if (_appliedPath is null
            || !string.Equals(Path.GetFileName(_appliedPath), fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string path = Paths.ResolveWallpaperFile(fileName);
        _appliedPath = path;

        // Un-favouriting takes the list with it - the picture is back in the daily
        // cache and there is no folder left to step through. Favouriting does not do
        // the reverse: starring a picture on the recent tab is not the same as having
        // gone to the favourites tab to pick one, so the rows stay where they were.
        _steppingFavorites = _steppingFavorites && Favorites.Contains(fileName);

        WallpaperService.Apply(path, _config.Fit);
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

    /// <summary>
    /// Title and source link of the pinned picture, read back from favorites.txt.
    ///
    /// <para>
    /// This is a third reader of that file, and the only one outside the picker - so
    /// it is fenced in. It runs when the pin is set, *and* the picture is not in the
    /// current eight day list, *and* a file of that name is in favorites\: three
    /// conditions that are rarely all true at once, and none of which opens the file
    /// to be answered. Without them this would be an unconditional file read on the
    /// startup path, which is exactly what the favourites are designed not to need.
    /// </para>
    /// <para>
    /// Cached against the file name it was read for, because UpdateMenuState runs on
    /// every refresh and every menu change while the pin only moves when the user
    /// moves it.
    /// </para>
    /// </summary>
    /// <param name="inFavorites">
    /// <see cref="InFavoriteMode"/>, already answered by the caller. Passed in rather
    /// than asked again so that one menu update probes the folder once.
    /// </param>
    private void EnsurePinnedMetadata(bool inFavorites)
    {
        string fileName = _config.PinnedWallpaper;
        if (!inFavorites || _currentIndex >= 0)
        {
            _pinnedMetadataFor = string.Empty;
            _pinnedTitle = null;
            _pinnedLink = null;
            return;
        }

        if (string.Equals(_pinnedMetadataFor, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pinnedMetadataFor = fileName;
        _pinnedTitle = null;
        _pinnedLink = null;
        if (Favorites.TryGetMetadata(fileName, out string title, out string link))
        {
            _pinnedTitle = title;
            _pinnedLink = link;
            Logger.Debug("pin: title recovered from the favourites file=" + fileName);
        }
    }

    /// <summary>
    /// Where "open the image source" goes: the current metadata when there is any, and
    /// otherwise whatever the favourites file remembered about the pinned picture.
    /// </summary>
    private string? CurrentCopyrightLink
        => string.IsNullOrWhiteSpace(_appliedImage?.CopyrightLink) ? _pinnedLink : _appliedImage!.CopyrightLink;

    private void UpdateMenuState()
    {
        bool pinned = _config.IsPinned;
        bool inFavorites = InFavoriteMode;
        EnsurePinnedMetadata(inFavorites);

        // The tooltip names the picture and nothing else: whether it is locked is
        // what the menu is for, and repeating it here only eats into the 63
        // characters the shell gives a tray tooltip.
        if (_appliedImage is not null)
        {
            // The title row is the widest thing in the menu and therefore what sets
            // its width. 48 characters is about as far as the menu can grow before it
            // stops looking like a tray menu; the full title is a hover away in the
            // tooltip and spelled out in the picker.
            string line = BracketDate(_appliedImage.DisplayDate, pinned) + " · " + _appliedImage.DisplayTitle;
            _titleItem.Text = EscapeMnemonic(Truncate(line, 48));
            _tray.Text = Truncate("必应壁纸 · " + _appliedImage.DisplayTitle, 63);
        }
        else if (pinned && _appliedPath is not null)
        {
            // Locked long enough to have left the eight day window, so the file itself
            // is all the metadata there is - unless the picture is a favourite, in
            // which case its title was written down on the day it still had one.
            // Described twice on purpose: the menu row brackets the date, the tooltip
            // is the one place that stays silent about the lock.
            //
            // _appliedPath rather than the pinned file name: the two name the same
            // picture, and this is the one of them that already knows which folder it
            // ended up in, which the write time has to be read from.
            //
            // Described once for both rows: they differ only in the brackets, and a
            // name with no date in it costs a stat to describe - twice would be twice.
            Favorites.DescribeFile(_appliedPath, out string date, out string named);
            bool remembered = !string.IsNullOrEmpty(_pinnedTitle);

            _titleItem.Text = EscapeMnemonic(Truncate(
                DescribeWallpaper(date, remembered ? _pinnedTitle! : named, locked: true),
                48));
            _tray.Text = Truncate(
                "必应壁纸 · " + (remembered ? _pinnedTitle! : DescribeWallpaper(date, named, locked: false)),
                63);
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
        _titleItem.Enabled = !_busy && !string.IsNullOrWhiteSpace(CurrentCopyrightLink);

        if (inFavorites)
        {
            // Both rows stay live. Whether there is a neighbour is only knowable by
            // enumerating favorites\, and this method runs on every refresh and every
            // busy flip - a directory read per grey pixel is the wrong trade. Clicking
            // past either end is a no-op instead (see MoveWithinFavorites).
            _newerItem.Enabled = !_busy;
            _olderItem.Enabled = !_busy;
        }
        else
        {
            // _currentIndex == -1 means the wallpaper is not in the list at all: there
            // is a newer picture to go to, but nothing older.
            _newerItem.Enabled = !_busy && _images.Count > 0 && _currentIndex != 0;
            _olderItem.Enabled = !_busy && _currentIndex >= 0 && _currentIndex < _images.Count - 1;
        }

        _refreshItem.Enabled = !_busy;
        _pickerItem.Enabled = !_busy;

        _pinItem.Checked = pinned;
        _pinItem.Enabled = !_busy && (pinned || _appliedPath is not null);

        // The picker paints the same state on a tile, and it can be open while this
        // runs - stepping through the list from the tray menu moves both badges.
        _pickerForm?.RefreshCurrentMarker();
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

    private void ShowPicker()
    {
        if (_pickerForm is null || _pickerForm.IsDisposed)
        {
            _pickerForm = new PickerForm(this);
        }

        ShowForm(_pickerForm);
        _pickerForm.LoadImages(_images);
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
        Logger.Info("settings: changed kind=" + e.Kind);
        switch (e.Kind)
        {
            case SettingKind.Market:
            case SettingKind.Resolution:
                // Neither releases the pin. What was locked is a photo; a market is only
                // the channel its metadata arrives through and a resolution only how
                // large a copy is kept, so releasing the lock for either undid what the
                // user had just asked for. A pinned desktop stays as it is until the
                // lock is lifted - EnsurePinnedAsync, not this, decides what happens to
                // it from here.
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
                Logger.Debug("refresh: timer interval=" + _config.RefreshIntervalHours + "h");
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
            Logger.Error("shell: opening the wallpaper folder failed", ex);
        }
    }

    private void OpenCopyrightLink()
    {
        string? link = CurrentCopyrightLink;
        if (string.IsNullOrWhiteSpace(link))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
            Logger.Info("shell: opened image source url=" + link);
        }
        catch (Exception ex)
        {
            Logger.Error("shell: opening the image source failed", ex);
        }
    }

    private void ExitApplication()
    {
        Logger.Info("shutdown: exit requested from the tray menu");
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
    /// Menu caption for a picture whose metadata is out of reach: date first, then
    /// whatever else there is to call it.
    ///
    /// <para>
    /// Described by <see cref="Favorites.DescribeFile"/> rather than by
    /// BingImageInfo.TryParseFileName, which only knows the three segment name this
    /// program writes. A picture the user dropped into favorites\ is named however
    /// they named it, and 20210606.jpg used to fall past every branch here and arrive
    /// as a bare "20210606" - no date, and no brackets to say it was locked, in
    /// exactly the case where the user is most likely to be looking. The picker had
    /// been reading such names for a while; this row now asks it rather than guess.
    /// </para>
    /// </summary>
    /// <param name="date">
    /// yyyy-MM-dd, or empty when the file could not be dated at all.
    /// </param>
    /// <param name="caption">
    /// What favorites.txt remembered, when it remembered anything. What the name says
    /// besides the date otherwise, which for one of ours is the image id - and empty
    /// when the date was the whole of the name.
    /// </param>
    private static string DescribeWallpaper(string date, string caption, bool locked)
    {
        // A name that was nothing but its date has no title to put after it: the row
        // would otherwise say the sixth of June twice.
        if (caption.Length == 0)
        {
            return BracketDate(date, locked) + " 的壁纸";
        }

        // Only when the write time could not be read either, which leaves nothing to
        // bracket. The ticked menu row below is then the one thing saying it is locked.
        return date.Length == 0
            ? caption
            : BracketDate(date, locked) + " · " + caption;
    }

    /// <summary>
    /// Brackets the date of the title row while the wallpaper is locked. The state
    /// belongs where the eye starts reading, and the ticked "锁定当前壁纸" row right
    /// underneath is what the brackets refer to.
    /// </summary>
    private static string BracketDate(string date, bool locked) => locked ? "[" + date + "]" : date;

    /// <summary>
    /// A native popup menu, the way every classic tray application builds one:
    /// Windows draws it with the shell's own metrics, font and theme, so it looks
    /// like the Explorer context menu instead of a WinForms imitation of it - in
    /// both colour schemes, see DarkModeNative.SetAppMode.
    /// </summary>
    private ContextMenu BuildMenu() => new(new[]
    {
        _titleItem,
        new MenuItem("-"),
        _olderItem,
        _newerItem,
        _pickerItem,
        _refreshItem,
        _pinItem,
        new MenuItem("-"),
        _folderItem,
        new MenuItem("-"),
        _settingsItem,
        _exitItem,
    });

    /// <summary>
    /// Hands the tray icon a menu built on a fresh HMENU.
    ///
    /// Windows measures a popup menu once and caches the width on the menu handle
    /// itself; changing a caption afterwards goes through SetMenuItemInfo, which
    /// never invalidates it, and neither does removing and re-inserting the rows.
    /// So a menu only ever grew: one long picture title left it stretched for the
    /// rest of the session, even after switching back to a short one. Only a new
    /// handle starts measuring from scratch.
    ///
    /// The rows that carry state are reused: Clear() detaches them without disposing,
    /// so the emptied menu takes nothing with it when it goes and every caption, tick
    /// and enabled flag survives the move to the new handle.
    /// </summary>
    private void RecreateMenu()
    {
        ContextMenu stale = _menu;
        stale.MenuItems.Clear();
        _menu = BuildMenu();
        _tray.ContextMenu = _menu;
        stale.Dispose();
    }

    /// <summary>
    /// Doubles the ampersands of a caption that comes from the outside. A single "&amp;"
    /// is the mnemonic prefix of a native menu item: a picture titled "Black &amp; white"
    /// would otherwise lose it and underline the space behind it.
    /// </summary>
    private static string EscapeMnemonic(string value) => value.Replace("&", "&&");

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
                Logger.Debug("theme: WM_SETTINGCHANGE/ImmersiveColorSet received");
                SystemColorSchemeChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        base.WndProc(ref m);
    }
}
