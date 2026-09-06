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
using Microsoft.Win32;

namespace BingWallpaper.UI;

/// <summary>
/// The application itself: a tray icon, a timer and the refresh logic.
/// There is no main window - the message loop is hosted by an
/// <see cref="ApplicationContext"/> plus a hidden window used for broadcasts.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    /// <summary>
    /// How wide the title row is allowed to make the menu, in characters.
    ///
    /// <para>
    /// It is the widest row and therefore the one that sets the width of the whole
    /// menu, and a menu wider than the eight short rows underneath it stops reading
    /// as a tray menu at all. The full title is a hover away in the tooltip and
    /// spelled out in the picker, so the row can afford to be the short one.
    /// </para>
    /// </summary>
    private const int MenuTitleLength = 36;

    /// <summary>
    /// What the title row says while a pass holds the desktop.
    ///
    /// <para>
    /// A constant because leaving the busy state has to be able to recognize its own
    /// caption. The row is otherwise left as it was found - that is what keeps a
    /// failure message on screen - and a pass that ends with no picture to name would
    /// go on claiming to be working until some later apply wrote over it.
    /// </para>
    /// </summary>
    private const string BusyTitle = "正在处理…";

    private readonly AppConfig _config;
    private readonly BingClient _client = new();
    private readonly ThumbnailCache _thumbnails;
    private readonly HiddenWindow _window;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>
    /// Drives the random rotation. Separate from <see cref="_timer"/> rather than
    /// folded into it: that one is the hourly metadata check and this one runs in
    /// minutes, and in this mode neither wants what the other does - see
    /// <see cref="RunRefreshPassAsync"/>.
    /// </summary>
    private readonly System.Windows.Forms.Timer _shuffleTimer;

    private readonly ShufflePlaylist _playlist = new();

    // Replaced after every right click, see RecreateMenu.
    private ContextMenu _menu;

    private readonly MenuItem _titleItem;
    private readonly MenuItem _newerItem;
    private readonly MenuItem _olderItem;
    private readonly MenuItem _pickerItem;
    private readonly MenuItem _refreshItem;
    private readonly MenuItem _shuffleItem;
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
    /// Set while a favourite is being applied, wherever the click came from - the tray
    /// menu, the picker, or the rotation timer. Raised by the apply itself
    /// (<see cref="ApplyFavoriteCoreAsync"/>) so that no route to it can be the one
    /// that forgets. Separate from <see cref="_busy"/>, which greys the menu and
    /// belongs to the refresh: a favourite needs no network and should not make the
    /// program look busy for the third of a second it takes.
    /// </summary>
    private bool _applyingFavorite;

    /// <summary>
    /// Whether the session is locked, which holds the rotation still.
    ///
    /// <para>
    /// A field rather than a plain "stop the timer, start it again", because it is a
    /// condition and not an event: everything that would otherwise start the timer -
    /// turning the rotation on, changing its interval, a step made by hand - has to
    /// see it too, or the pause would be undone by whatever happened to run next.
    /// </para>
    /// </summary>
    private bool _sessionLocked;

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
    /// <see cref="ApplyFavoriteAsync"/> raises it, and <see cref="ApplyIndexAsync"/> -
    /// everything applied out of <see cref="_images"/> - clears it. The rest only
    /// clear it when its premise is gone: the pin released, the picture un-favourited,
    /// or a restart, which is where it starts life as a guess (see
    /// <see cref="RestorePinnedWallpaper"/>) because it is deliberately not persisted.
    /// </para>
    /// </summary>
    private bool _steppingFavorites;

    // File name the two below were read for; empty when nothing is cached.
    private string _appliedMetadataFor = string.Empty;
    private string? _appliedTitle;
    private string? _appliedLink;

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
        _shuffleItem = new MenuItem("随机轮播", (_, _) => SetShuffle(!_config.Shuffle));
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

        // Started below rather than here: it is only running in one of the three
        // modes, and the first draw has to wait for the message loop anyway.
        _shuffleTimer = new System.Windows.Forms.Timer { Interval = GetShuffleIntervalMilliseconds() };
        _shuffleTimer.Tick += (_, _) => StepShuffle(forward: true);

        ThemeManager.ThemeChanged += OnThemeChanged;

        try
        {
            // Documented BCL, but it builds a window and a thread of its own the first
            // time anyone subscribes, and that can fail in a session that has no
            // desktop to talk to. Degrading here costs the pause and nothing else.
            SystemEvents.SessionSwitch += OnSessionSwitch;
        }
        catch (Exception ex)
        {
            Logger.Warn("shuffle: the session listener could not be attached error=" + ex.Message);
        }

        // Before the first network call: from here on the cleanup passes and the
        // menu have something to work with even while the metadata request is still
        // in flight.
        RestorePinnedWallpaper();

        // Run the first check as soon as the message loop starts - and, when the
        // rotation is on, draw a picture straight away instead of at the end of the
        // first interval: what someone turns it on for is that the wallpaper changes.
        // Both need the loop running, the refresh for its synchronization context and
        // the draw for the fade, which parents a window of its own.
        _window.BeginInvoke(new Action(() =>
        {
            // Through the gate rather than starting the timer here: the session
            // listener is attached above, before this action is queued, so a machine
            // that was locked during startup has already posted SetSessionLocked ahead
            // of it - and a round must not be spent on a screen nobody is looking at.
            RestartShuffleTimer();
            if (_config.Shuffle && !_sessionLocked)
            {
                StepShuffle(forward: true);
            }

            // After the step and not before it: StartRefresh runs synchronously up to
            // its first await, so it has already raised _busy by the time it returns,
            // and a step taken behind it hits the guard in StepShuffle. Dropped steps
            // are not retried, so the desktop kept whatever the last session left on it
            // until the first tick - a whole interval, up to a day - with the menu
            // greyed out behind BusyTitle the entire time. Nothing is contended by
            // going first: a refresh in rotation mode only fills the cache, and the
            // cleanup passes behind it never look inside favorites\.
            StartRefresh(userInitiated: false);
        }));
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

    /// <summary>
    /// Whether a refresh pass owns the program. What greys the tray menu out, and with
    /// it the picker's rotation button - the two switch the same thing and must not
    /// disagree about whether it can be switched.
    /// </summary>
    public bool IsBusy => _busy;

    public CancellationToken ShutdownToken => _shutdown.Token;

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
            await WallpaperService
                .ApplyAsync(path, _config.Fit, _config.FadeTransition, _appliedPath)
                .ConfigureAwait(true);
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
                // A static event, so the subscription outlives every reference to this
                // object. Detached before the window below goes, which is what the
                // handler marshals through.
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch (Exception ex)
            {
                Logger.Debug("shutdown: detaching the session listener failed error=" + ex.Message);
            }

            // A fade still running owns a window parented into Explorer's desktop, on
            // a thread of its own. This ends that thread and waits for it, so the
            // input queue attachment the child window created is gone before the
            // process is - with a timeout, because it is Explorer on the other end.
            WallpaperTransition.Cancel();

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
            _shuffleTimer.Stop();
            _shuffleTimer.Dispose();
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
    /// Holds the rotation still while the session is locked.
    ///
    /// <para>
    /// Not for the cost of a change - a few hundred milliseconds of transcode spread
    /// over an interval measured in minutes is nothing. It is for the round: a
    /// rotation left running against a locked screen spends its way through a shuffle
    /// nobody is watching, so what is on the desktop on the way back is whichever
    /// picture the clock happened to stop on. Paused, the user comes back to the one
    /// they left, and it gets a full interval from there.
    /// </para>
    /// <para>
    /// The lock and nothing else. Fast user switching and a disconnected remote
    /// session hide the desktop just as thoroughly and would pause on the same
    /// reasoning; they are left out because the screen locking on its idle timeout is
    /// how an unattended machine gets that way, while taking the others on means
    /// deciding what a session that is disconnected *and* locked does on each of the
    /// events it can come back through - a pair of states, not one.
    /// </para>
    /// </summary>
    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        bool locked;
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
                locked = true;
                break;

            case SessionSwitchReason.SessionUnlock:
                locked = false;
                break;

            default:
                return;
        }

        try
        {
            // SystemEvents raises this on a thread of its own, so the timer and the
            // flag - both of which belong to the UI thread - are reached through the
            // hidden window, the way the single instance listener reaches ShowSettings.
            _window.BeginInvoke(new Action(() => SetSessionLocked(locked)));
        }
        catch (Exception ex)
        {
            // The window is gone, which means the process is on its way out and there
            // is no rotation left to pause.
            Logger.Debug("shuffle: the session switch could not be posted error=" + ex.Message);
        }
    }

    /// <summary>Applies a session lock or unlock to the rotation, on the UI thread.</summary>
    private void SetSessionLocked(bool locked)
    {
        if (_disposed || _sessionLocked == locked)
        {
            return;
        }

        _sessionLocked = locked;

        // The refresh timer is deliberately left running: it fetches metadata and
        // prunes the cache, which are worth doing whether or not anyone is looking,
        // and the only mode where it touches the desktop is the one where the
        // rotation is off.
        if (locked)
        {
            _shuffleTimer.Stop();
        }
        else
        {
            RestartShuffleTimer();
        }

        if (_config.Shuffle)
        {
            Logger.Info(locked ? "shuffle: paused, session locked" : "shuffle: resumed, session unlocked");
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
            else if (_config.Shuffle)
            {
                // The desktop belongs to the rotation, so this pass only keeps the
                // cache current: today's picture is downloaded so it can be starred
                // from the recent tab, and the two passes below still have a settled
                // folder to work on. Deliberately not applied - and EnsureCachedAsync
                // leaves _currentIndex and _appliedImage alone, so the menu keeps
                // describing the picture the rotation put up.
                if (images.Count > 0)
                {
                    await EnsureCachedAsync(images[0]).ConfigureAwait(true);
                }
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

        // Locking a picture is the natural way out of the rotation - it is what "stop
        // here, I like this one" looks like - so every path that sets a lock ends it,
        // the picker's included. Written in this save rather than through SetShuffle,
        // which would put a second write of the INI file behind the one click.
        bool stopShuffle = value.Length > 0 && _config.Shuffle;
        if (!stopShuffle && string.Equals(_config.PinnedWallpaper, value, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string previous = _config.PinnedWallpaper;
        bool previousShuffle = _config.Shuffle;
        _config.PinnedWallpaper = value;
        if (stopShuffle)
        {
            _config.Shuffle = false;
        }

        try
        {
            _config.Save(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            _config.PinnedWallpaper = previous;
            _config.Shuffle = previousShuffle;
            Logger.Error("pin: saving the configuration failed", ex);
            ErrorDialog.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        if (stopShuffle)
        {
            // Only the rotation is torn down here, never the wallpaper: the picture
            // it last put up is the one being locked.
            _shuffleTimer.Stop();
            _playlist.Clear();
            Logger.Info("shuffle: disabled, the wallpaper was locked");
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
            ReleasePin();
            return;
        }

        if (_appliedPath is null)
        {
            return;
        }

        SetPinned(Path.GetFileName(_appliedPath));
    }

    /// <summary>
    /// Lifts the lock and hands the desktop back to the refresh timer. Public for the
    /// picker's context menu, which needs this direction only: the row is offered on
    /// the locked tile alone, so there is nothing there to toggle.
    /// </summary>
    public void ReleasePin()
    {
        if (!_config.IsPinned)
        {
            return;
        }

        SetPinned(null);
        if (_config.IsPinned)
        {
            // The save failed, nothing was released.
            return;
        }

        ReturnToDailyWallpaper();
    }

    /// <summary>
    /// Hands the desktop back to the refresh timer. Both of the other two modes end
    /// here, because both are the same thing to leave: something that was deciding
    /// the wallpaper has stopped, and today's picture is what that falls back to.
    /// </summary>
    private void ReturnToDailyWallpaper()
    {
        // Restart the timer so the first automatic change is a full interval away,
        // and go to today's picture now rather than at some arbitrary point within
        // the hour.
        _timer.Stop();
        _timer.Start();

        if (HasTodaysMetadata())
        {
            // The list already names today's picture, so fetching it again could only
            // return the same entry. Applying it straight from the cache keeps this
            // off the network entirely. Skipping the refresh also skips its cleanup
            // pass, which is what would drop the file that just lost its protection -
            // the next cycle does that.
            _ = MoveToAsync(0, pinAfterwards: false);
            return;
        }

        StartRefresh(userInitiated: true);
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
        if (_config.Shuffle)
        {
            // Asked before InFavoriteMode and not through it: the rotation runs with
            // no pin set, which is the very thing InFavoriteMode reads, so both rows
            // would otherwise fall through to the eight day window.
            //
            // A negative delta is "下一张", which walks the list towards the newer
            // end. A shuffled round has no newer or older, only a play position, and
            // the one that row steps towards is forward.
            StepShuffle(forward: delta < 0);
            return;
        }

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
    /// through the same path the picker uses and never raises _busy. The answer this
    /// returns is "did the click belong to the folder", which is known before the
    /// apply behind it finishes - so the apply is started and not waited for.
    /// </para>
    /// </summary>
    private bool MoveWithinFavorites(int delta)
    {
        if (_busy || _applyingFavorite)
        {
            // The rows are greyed while busy, but the menu was measured before the
            // refresh started and the click can still land. Dropped the way
            // MoveToAsync drops it, and reported as handled either way: the 8 day
            // path would only reach the same guard.
            //
            // _applyingFavorite is the same guard for the step before this one, which
            // no longer finishes inside the click that started it. Reaching it takes
            // reopening the menu inside half a second, so this is a rail rather than a
            // throttle - the picker is where clicks actually arrive in bursts, and it
            // remembers the last one instead of dropping it.
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
        _ = StepIntoFavoriteAsync(items[target].FileName);
        return true;
    }

    /// <summary>
    /// Applies one favourite for <see cref="MoveWithinFavorites"/> and reports a
    /// failure the way the menu has no other way to: nobody is awaiting the task, so
    /// the dialog has to be raised from inside it.
    /// </summary>
    private async Task StepIntoFavoriteAsync(string fileName)
    {
        try
        {
            // _applyingFavorite is raised by the apply itself, so that the picker's
            // route to it is covered by the same guard - see ApplyFavoriteCoreAsync.
            if (!await ApplyFavoriteAsync(fileName).ConfigureAwait(true))
            {
                ErrorDialog.Show("切换壁纸失败", "详见日志文件。");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("switch: stepping through the favourites failed", ex);
            ErrorDialog.Show("切换壁纸失败", Logger.Describe(ex));
        }
    }

    /// <summary>
    /// The only writer of <see cref="AppConfig.Shuffle"/> - the tray menu row and the
    /// picker's header button both come here. Like <see cref="SetPinned"/> the value in
    /// memory changes only once it is on disk, so a failed save leaves the program and
    /// the configuration file saying the same thing.
    /// </summary>
    public void SetShuffle(bool enabled)
    {
        if (_config.Shuffle == enabled)
        {
            return;
        }

        _config.Shuffle = enabled;
        try
        {
            _config.Save(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            _config.Shuffle = !enabled;
            Logger.Error("shuffle: saving the configuration failed", ex);
            ErrorDialog.Show("保存设置失败", Logger.Describe(ex));
            return;
        }

        OnShuffleModeChanged();
    }

    /// <summary>
    /// Brings the rotation into line with <see cref="AppConfig.Shuffle"/>, whichever
    /// of the two ways it was just changed - the menu row or the settings window.
    /// </summary>
    private void OnShuffleModeChanged()
    {
        if (!_config.Shuffle)
        {
            _shuffleTimer.Stop();
            _playlist.Clear();
            Logger.Info("shuffle: disabled");
            UpdateMenuState();

            // Something that was deciding the wallpaper has stopped, which is the same
            // situation releasing a lock leaves behind, so it ends the same way.
            ReturnToDailyWallpaper();
            return;
        }

        // Releasing the lock rather than refusing to start: the two are mutually
        // exclusive, and the click that arrived is the newer instruction. Unlike the
        // reverse direction in SetPinned this cannot share the save - the settings
        // window has already written the file by the time it gets here - but it costs
        // nothing in the common case, where SetPinned returns without writing.
        SetPinned(null);
        if (_config.IsPinned)
        {
            // The save failed and SetPinned has already said so. The lock still
            // stands, so the rotation must not start: the flag goes back by hand
            // rather than through SetShuffle, which would retry the save that just
            // failed and, were it to get through, move the desktop off the very
            // picture that is still locked. The file is left saying Shuffle=true,
            // which Load normalizes to this same state on the next start.
            _config.Shuffle = false;
            UpdateMenuState();
            return;
        }

        _playlist.Clear();
        _shuffleTimer.Interval = GetShuffleIntervalMilliseconds();
        RestartShuffleTimer();
        Logger.Info("shuffle: enabled interval=" + _config.ShuffleIntervalMinutes + "m");

        // Before the step and not left to it: the step may find nothing to do, and the
        // tick on the menu row has to be right either way.
        UpdateMenuState();

        if (!_sessionLocked && !StepShuffle(forward: true) && _playlist.Count == 0)
        {
            // A rotation with nothing to rotate does nothing at all, which from the
            // outside is indistinguishable from the click not having registered. Said
            // in a balloon rather than a dialog: the shell draws it, the way it draws
            // the menu this was clicked in, and nothing here is worth a modal window.
            _tray.ShowBalloonTip(
                5000,
                "随机轮播",
                "收藏夹是空的。在「选择壁纸」里收藏几张之后才能进行随机轮播。",
                ToolTipIcon.Info);
        }
    }

    /// <summary>
    /// Moves the rotation one picture and puts it on the desktop. Reports whether
    /// there was anywhere to move to.
    /// </summary>
    private bool StepShuffle(bool forward)
    {
        // Ahead of the guard below, cheap enough that a dropped step can afford it:
        // it keeps the round in step with the folder whatever happens next, and it is
        // what makes Count mean "how many favourites are there" to a caller reading it
        // after a false.
        _playlist.Sync(Favorites.Scan());

        if (_busy || _applyingFavorite)
        {
            // Something else is already deciding the wallpaper. Dropped rather than
            // queued, the way MoveWithinFavorites drops one: the next tick is along
            // shortly, and the only way to reach this by hand is to reopen the menu
            // inside the third of a second an apply takes.
            //
            // _busy is a download the picker or the refresh started, which ends by
            // applying - and possibly locking - the picture the user asked for. A step
            // taken across it finishes last and wins the desktop, which would leave the
            // lock on one picture and the menu and the desktop on another.
            Logger.Debug("shuffle: step dropped, another apply is running busy=" + _busy);
            return false;
        }

        string? fileName = forward ? _playlist.Next() : _playlist.Previous();
        if (fileName is null)
        {
            // An empty folder, or the start of the round with nothing behind it.
            Logger.Debug("shuffle: nothing to step to forward=" + forward + " count=" + _playlist.Count);
            return false;
        }

        // Re-applying the picture that is already on the desktop is a full transcode
        // for no visible change - and with a single favourite it is every tick.
        if (_appliedPath is not null
            && string.Equals(Path.GetFileName(_appliedPath), fileName, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Debug("shuffle: already on the desktop file=" + fileName);
            RestartShuffleTimer();
            return true;
        }

        Logger.Info(
            "shuffle: applying file=" + fileName +
            " position=" + _playlist.Position + "/" + _playlist.Count);

        // Before the apply, not after: the apply is not awaited, and a step made by
        // hand should get a whole interval to itself either way.
        RestartShuffleTimer();
        _ = ShuffleIntoAsync(fileName);
        return true;
    }

    /// <summary>
    /// Applies one picture for the rotation.
    ///
    /// <para>
    /// Unlike <see cref="StepIntoFavoriteAsync"/> a failure raises no dialog. Almost
    /// every one of them is the same race - the file left favorites\ between the scan
    /// and the apply - and the answer to it is the next step, which draws from a list
    /// the folder has been re-read into. A modal window that can appear on a timer
    /// while nobody is at the machine would be the worse failure of the two.
    /// </para>
    /// </summary>
    private async Task ShuffleIntoAsync(string fileName)
    {
        try
        {
            if (await ApplyFavoriteCoreAsync(fileName).ConfigureAwait(true))
            {
                UpdateMenuState();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("shuffle: applying failed file=" + fileName, ex);
        }
    }

    /// <summary>
    /// Gives the current picture a full interval, when there is a rotation to give it
    /// to. Stop then Start, not Enabled: an already running timer keeps counting from
    /// where it was, so a step made by hand would otherwise be replaced by the
    /// rotation moments later.
    ///
    /// <para>
    /// The single gate every start of the timer goes through, which is what keeps the
    /// two conditions that hold it still - the rotation being off and the session
    /// being locked - from having to be repeated at each call site.
    /// </para>
    /// </summary>
    private void RestartShuffleTimer()
    {
        if (!_config.Shuffle || _sessionLocked)
        {
            return;
        }

        _shuffleTimer.Stop();
        _shuffleTimer.Start();
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
    /// <para>
    /// Nothing here downloads - a favourite is on disk by definition - but the apply
    /// itself is awaited all the same: it is what puts SystemParametersInfoW on a
    /// thread pool thread, which is where a hundreds of milliseconds long transcode
    /// belongs.
    /// </para>
    /// </summary>
    public async Task<bool> ApplyFavoriteAsync(string fileName)
    {
        if (!await ApplyFavoriteCoreAsync(fileName).ConfigureAwait(true))
        {
            return false;
        }

        // The one place stepping switches to the folder, and note it is set even when
        // FindImageIndex found the picture in the window as well: the click was on the
        // favourites tab, and that is the whole question.
        _steppingFavorites = true;
        SetPinned(fileName);
        UpdateMenuState();
        return true;
    }

    /// <summary>
    /// Puts a favourite on the desktop and nothing else - no lock, no stepping list.
    ///
    /// <para>
    /// Split out for the rotation, which applies a picture every few minutes and must
    /// not do either: the lock is a different mode and would rewrite the INI file on
    /// every change, and the stepping list is about which folder a click walks, which
    /// the rotation answers for itself.
    /// </para>
    /// <para>
    /// Holder of <see cref="_applyingFavorite"/> for all three routes that reach it,
    /// rather than each of them raising it around the call. The picker is why: it
    /// applies a favourite through the public wrapper above and went near neither the
    /// flag nor <see cref="_busy"/>, so a rotation tick landing inside the third of a
    /// second that apply takes sailed past both guards - and, having started later,
    /// finished later, leaving the lock on the picture that was clicked and the
    /// desktop on the one the rotation drew.
    /// </para>
    /// </summary>
    private async Task<bool> ApplyFavoriteCoreAsync(string fileName)
    {
        string path = Paths.ResolveWallpaperFile(fileName);
        if (!File.Exists(path))
        {
            Logger.Warn("apply: the favourite is gone file=" + fileName);
            return false;
        }

        _applyingFavorite = true;
        try
        {
            if (!await WallpaperService
                    .ApplyAsync(path, _config.Fit, _config.FadeTransition, _appliedPath)
                    .ConfigureAwait(true))
            {
                return false;
            }

            _appliedPath = path;
            _currentIndex = FindImageIndex(fileName);
            _appliedImage = _currentIndex >= 0 ? _images[_currentIndex] : null;
            return true;
        }
        finally
        {
            _applyingFavorite = false;
        }
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
    /// Title and source link of the wallpaper on the desktop, read back from
    /// favorites.txt for the case where the eight day list cannot supply them.
    ///
    /// <para>
    /// This is a third reader of that file, and the only one outside the picker - so
    /// it is fenced in. It runs only when there is a wallpaper *and* it is not in the
    /// current eight day list, and then at most once per picture, because the answer
    /// is cached against the file name it was read for while UpdateMenuState runs on
    /// every refresh and every busy flip.
    /// </para>
    /// <para>
    /// It used to be fenced in harder still, by the pin: keyed on
    /// <see cref="AppConfig.PinnedWallpaper"/> and gated on the stepping list pointing
    /// at favorites\. The rotation broke both premises - it runs with no pin set at
    /// all - and every favourite it put up lost its title. Asking about the applied
    /// picture instead covers the pin as well, since a locked picture out of the
    /// window is an applied picture out of the window. The gate it drops was worth one
    /// read of a few kilobytes, once, for a locked picture that is not a favourite.
    /// </para>
    /// </summary>
    private void EnsureAppliedMetadata()
    {
        // Empty whenever the list can answer for itself, which is also what clears a
        // stale title when the wallpaper moves back into the window.
        string fileName = _currentIndex < 0 && _appliedPath is not null
            ? Path.GetFileName(_appliedPath)
            : string.Empty;

        if (string.Equals(_appliedMetadataFor, fileName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appliedMetadataFor = fileName;
        _appliedTitle = null;
        _appliedLink = null;
        if (fileName.Length != 0 && Favorites.TryGetMetadata(fileName, out string title, out string link))
        {
            _appliedTitle = title;
            _appliedLink = link;
            Logger.Debug("wallpaper: title recovered from the favourites file=" + fileName);
        }
    }

    /// <summary>
    /// Where "open the image source" goes: the current metadata when there is any, and
    /// otherwise whatever the favourites file remembered about the pinned picture.
    /// </summary>
    private string? CurrentCopyrightLink
        => string.IsNullOrWhiteSpace(_appliedImage?.CopyrightLink) ? _appliedLink : _appliedImage!.CopyrightLink;

    private void UpdateMenuState()
    {
        bool pinned = _config.IsPinned;
        bool shuffling = _config.Shuffle;
        bool inFavorites = InFavoriteMode;
        EnsureAppliedMetadata();

        // The tooltip names the picture and nothing else: whether it is locked is
        // what the menu is for, and repeating it here only eats into the 63
        // characters the shell gives a tray tooltip.
        if (_appliedImage is not null)
        {
            // A Bing title is a sentence written to be read, so half of one still
            // says something: it is cut at MenuTitleLength rather than dropped.
            string line = BracketDate(_appliedImage.DisplayDate, pinned) + " · " + _appliedImage.DisplayTitle;
            _titleItem.Text = EscapeMnemonic(Truncate(line, MenuTitleLength));
            _tray.Text = Truncate("必应壁纸 · " + _appliedImage.DisplayTitle, 63);
        }
        else if (_appliedPath is not null)
        {
            // Out of the eight day window - locked there long enough, or drawn there
            // by the rotation - so the file itself is all the metadata there is,
            // unless the picture is a favourite, in which case its title was written
            // down on the day it still had one. Described twice on purpose: the menu
            // row brackets the date when the picture is locked, the tooltip never says
            // so. Locked, not always: the rotation reaches this branch with nothing
            // locked at all, and the brackets refer to a menu row that is not ticked
            // then.
            //
            // _appliedPath rather than the pinned file name: the two name the same
            // picture, and this is the one of them that already knows which folder it
            // ended up in, which the write time has to be read from.
            //
            // Described once for both rows: they are built from the same two pieces,
            // and a name with no date in it costs a stat to describe - twice would be
            // twice.
            Favorites.DescribeFile(_appliedPath, out string date, out string named);
            bool remembered = !string.IsNullOrEmpty(_appliedTitle);

            // A caption taken from the file name says nothing once it is cut: the
            // half of "Space_91_OBGA.AdobeStock_4803068…" that survives is not a
            // title, just noise the eye has to step over on its way to the date. So
            // it is dropped rather than truncated, leaving the row saying the one
            // thing it still knows - and the whole name is a hover away in the
            // tooltip, which is wider. A title favorites.txt remembered is words
            // someone wrote, and is truncated like any other title; a name that fits
            // is shown whole either way.
            string line = DescribeWallpaper(date, remembered ? _appliedTitle! : named, locked: pinned);
            if (!remembered && date.Length != 0 && line.Length > MenuTitleLength)
            {
                line = DescribeWallpaper(date, string.Empty, locked: pinned);
            }

            _titleItem.Text = EscapeMnemonic(Truncate(line, MenuTitleLength));
            _tray.Text = Truncate(
                "必应壁纸 · " + (remembered ? _appliedTitle! : DescribeWallpaper(date, named, locked: false)),
                63);
        }
        else if (!_busy)
        {
            // Nothing has reached the desktop this session, so the row can only say
            // why. Whatever it holds was written by the pass that just ended and is
            // kept - "刷新失败，详见日志文件" is the one worth keeping - with the one
            // exception of BusyTitle, which described that pass and does not outlive
            // it: left standing it tells the user the program is working while nothing
            // is running, and nothing writes over it until the next apply, which in the
            // rotation is a whole interval away.
            if (_images.Count == 0)
            {
                _titleItem.Text = "尚未获取到壁纸信息";
            }
            else if (string.Equals(_titleItem.Text, BusyTitle, StringComparison.Ordinal))
            {
                _titleItem.Text = shuffling && _playlist.Count == 0
                    ? "收藏夹是空的，无法轮播"
                    : "尚未应用壁纸，详见日志文件";
            }
        }

        if (_busy)
        {
            _titleItem.Text = BusyTitle;
        }

        // Clickable only when there is somewhere to go: no link, or a title that
        // currently says something else, means the row is just a caption.
        _titleItem.Enabled = !_busy && !string.IsNullOrWhiteSpace(CurrentCopyrightLink);

        if (shuffling)
        {
            // The one list whose ends can be drawn rather than discovered by clicking:
            // the play position is a field, so no folder has to be read to answer this.
            // Forward never ends - the last picture of a round is followed by another
            // shuffle - while backwards stops at the start of the round.
            _newerItem.Enabled = !_busy;
            _olderItem.Enabled = !_busy && _playlist.HasPrevious;
        }
        else if (inFavorites)
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

        _shuffleItem.Checked = shuffling;
        _shuffleItem.Enabled = !_busy;

        _pinItem.Checked = pinned;
        _pinItem.Enabled = !_busy && (pinned || _appliedPath is not null);

        // The picker paints the same state on a tile, and it can be open while this
        // runs - stepping through the list from the tray menu moves both badges.
        _pickerForm?.RefreshCurrentMarker();

        // The rotation has two switches: the menu row here - and the lock, which turns
        // it off - and the button above the picker's favourites. A window left open
        // would otherwise go on showing the state it was opened with.
        _pickerForm?.SyncShuffle();
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

            case SettingKind.ShuffleInterval:
                // Restarted rather than left counting: the new interval should be
                // measured from now, not from whenever the running one started.
                _shuffleTimer.Interval = GetShuffleIntervalMilliseconds();
                RestartShuffleTimer();
                Logger.Debug("shuffle: timer interval=" + _config.ShuffleIntervalMinutes + "m");
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

    private int GetShuffleIntervalMilliseconds()
    {
        int minutes = AppConfig.Clamp(
            _config.ShuffleIntervalMinutes,
            AppConfig.MinShuffleIntervalMinutes,
            AppConfig.MaxShuffleIntervalMinutes);
        return minutes * 60 * 1000;
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
        _shuffleItem,
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
