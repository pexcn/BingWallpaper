using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BingWallpaper;

/// <summary>
/// Crossfades the desktop when the wallpaper changes.
///
/// <para>
/// Windows has nothing to ask for here: SPI_SETDESKWALLPAPER tells Explorer to
/// repaint the wallpaper layer and that repaint is a cut. So the fade is drawn by
/// this program, and the trick is where. A window covering the screen would cover
/// the desktop icons with it, and one drawn on top of everything would cover the
/// windows the user is working in - so the cover goes *into* the wallpaper layer:
/// a layered child of the WorkerW that Explorer keeps behind the icons. Being in
/// that layer, it is hidden by every real window exactly like the wallpaper is,
/// and the icons stay on top of it and stay live.
/// </para>
/// <para>
/// The order is what makes it look like a crossfade. The cover is painted with the
/// picture that is already on screen and shown opaque, so nothing changes visibly;
/// the new wallpaper is applied underneath it, where it cannot be seen; then the
/// cover's alpha is walked down to zero and the new picture emerges through it.
/// Doing it this way round means the wallpaper is already correct the moment the
/// fade starts - if anything goes wrong from there, the worst case is a cut.
/// </para>
/// <para>
/// What the cover is painted with is the wallpaper layer's own pixels, copied out
/// of it with one blit. That is both the cheapest and the most faithful answer: no
/// file to read, no UHD JPEG to decode, no fit rule to reproduce, and the frame is
/// identical to what Explorer drew rather than merely close to it. It is also the
/// only answer - drawing the outgoing picture from its file was how this started,
/// and it went when the copy proved itself, because reproducing a layout Explorer
/// owns can only ever be approximately right and there are six of them.
/// </para>
/// <para>
/// So every fit crossfades, and a copy that comes back blank is a cut.
/// </para>
/// <para>
/// The cover lives on a thread of its own, which exists for as long as one fade
/// does. An animation has nothing to do with what the rest of the program is busy
/// with, and putting it on the main thread made it hostage to exactly that: a fade
/// is driven by WM_TIMER, the lowest priority message there is, so a queue holding
/// input or paints starves it - and a burst of clicks is a queue full of input.
/// Fades were lost entirely that way, without a trace, because a starved fade
/// still ends by its clock and simply never draws a frame. On its own thread there
/// is nothing else in the queue. It also confines the input queue attachment that
/// comes with parenting a window across a process boundary to a thread that lives
/// for half a second, instead of the one that runs the program.
/// </para>
/// <para>
/// A change that arrives while a fade is running is handed to that fade rather than
/// starting a new one: see <see cref="RunAsync"/>. If the fade had already started,
/// the cover first folds what is on screen into its own picture and goes opaque
/// again, which changes not one pixel and lets the new wallpaper go up out of sight
/// like every other one - so a click during a fade continues the transition rather
/// than cutting through it.
/// </para>
/// <para>
/// Between those moments the alpha is the only thing that moves: the frame is
/// uploaded once and DWM composes it, so a tick costs one byte rather than a full
/// screen blend. The blend happens once per change taken mid fade, never per frame.
/// </para>
/// <para>
/// None of this is documented by Microsoft, so every step is allowed to fail: no
/// Progman, no WorkerW, no device context, a blit that returns nothing - each one
/// logs and falls back to the plain cut, which is what the program did before.
/// </para>
/// </summary>
internal static class WallpaperTransition
{
    /// <summary>
    /// Bounds on how long the cover stays opaque before the fade starts.
    ///
    /// <para>
    /// SystemParametersInfoW returns once it has transcoded the picture, but Explorer
    /// paints it on its own thread afterwards and says nothing when it is done.
    /// Dropping the alpha before that paint lands would show the *old* wallpaper
    /// through the fade - the one thing this is supposed to hide.
    /// </para>
    /// <para>
    /// With nothing to wait for, the hold is guessed from the only measurement on
    /// hand: how long the transcode itself took. Both run on the same machine at the
    /// same moment, so a slow transcode is the signal that Explorer's paint will be
    /// slow too - which is exactly the case a fixed value gets wrong, since it is the
    /// downclocked machine that needs the longer hold and the idle one that must not
    /// pay for it.
    /// </para>
    /// </summary>
    private const int MinHoldMilliseconds = 150;

    /// <summary>Upper bound of the hold, see <see cref="MinHoldMilliseconds"/>.</summary>
    private const int MaxHoldMilliseconds = 500;

    /// <summary>
    /// How long the fade itself takes. Windows fades a slideshow wallpaper in about a
    /// second; this is well short of that, because a wallpaper change here is the
    /// answer to a click and a click wants an answer.
    /// </summary>
    private const int FadeMilliseconds = 500;

    /// <summary>
    /// Roughly 60 steps a second. The elapsed time drives the alpha, not the tick
    /// count, so a late tick costs smoothness and never correctness.
    /// </summary>
    private const int TickMilliseconds = 15;

    /// <summary>
    /// How long to wait for the cover to appear before giving up and cutting.
    ///
    /// <para>
    /// Generous, because it is never reached in the normal case - building the cover
    /// is a blit and a window - and because reaching it means the wallpaper does not
    /// change until it does. The one call in there that can wait on Explorer is the
    /// Progman message, and that one carries a one second timeout of its own.
    /// </para>
    /// </summary>
    private const int ShowTimeoutMilliseconds = 2000;

    /// <summary>How long shutdown waits for the fade thread to finish and let go.</summary>
    private const int JoinMilliseconds = 1000;

    /// <summary>
    /// Posted to the cover to start its fade, or to restart the hold when a second
    /// change arrives while it is still opaque. wParam carries the hold.
    /// </summary>
    private const int WM_FADE_BEGIN = NativeMethods.WM_APP + 1;

    /// <summary>Posted to the cover to take it down now and end its thread.</summary>
    private const int WM_FADE_CANCEL = NativeMethods.WM_APP + 2;

    /// <summary>
    /// Posted to the cover to pause its fade and go opaque again without changing
    /// what is on screen, so that a change can be applied out of sight. Answered, not
    /// just posted: the caller has to know it happened before it applies anything.
    /// </summary>
    private const int WM_FADE_HOLD = NativeMethods.WM_APP + 3;

    /// <summary>
    /// The fade in flight, if any. Only ever read and written on the thread that
    /// calls <see cref="RunAsync"/>; the fade thread reaches back through one volatile
    /// flag and the tasks on <see cref="Cover"/>, and nothing else.
    /// </summary>
    private static Cover? _active;

    /// <summary>
    /// Applies a wallpaper under a crossfade out of whatever is on the desktop now.
    ///
    /// <para>
    /// <paramref name="apply"/> is always called exactly once, whether or not the
    /// cover could be built, and its result is passed straight back: a caller cannot
    /// tell the difference between a faded change and a cut, and should not have to.
    /// It is awaited with the cover up and opaque, which is what lets it move the
    /// transcode off the UI thread without a frame of the new wallpaper showing.
    /// </para>
    /// <para>
    /// A change arriving while a fade is running does not start a second one. The
    /// cover already up shows what the desktop looked like before any of this began,
    /// and what it uncovers is whatever Explorer has painted by the time it is gone -
    /// so handing the change to it gives ten clicks in a row one smooth fade from the
    /// first picture to the last, instead of ten fades that each get killed by the
    /// next. It is also what the user asked for and cheaper than either alternative:
    /// the pictures in between are never drawn, only applied.
    /// </para>
    /// </summary>
    public static async Task<bool> RunAsync(Func<Task<bool>> apply)
    {
        Cover? running = GetRunning();
        if (running is not null)
        {
            // Opaque again first, and only then apply: a change made while the cover
            // is see-through is a change the user watches happen, which is the cut
            // this whole file exists to avoid.
            await running.HoldAsync().ConfigureAwait(true);
            if (running.IsRunning)
            {
                Logger.Debug("fade: relayed to the cover already up");
                try
                {
                    (bool Applied, long Milliseconds) relay =
                        await ApplyTimedAsync(apply).ConfigureAwait(true);

                    // Resumed whether or not it worked: on failure the desktop still
                    // holds the picture the cover is painted with, so fading out is
                    // invisible - and leaving the cover held would freeze the desktop.
                    running.BeginFade(GetHold(relay.Milliseconds));
                    return relay.Applied;
                }
                catch
                {
                    running.BeginFade(MinHoldMilliseconds);
                    throw;
                }
            }

            // It finished while it was being asked to hold. Nothing was applied yet,
            // so this falls through and gets a fade of its own.
            _active = null;
        }

        Cover cover = new Cover();
        if (!await cover.ShowAsync().ConfigureAwait(true))
        {
            return await apply().ConfigureAwait(true);
        }

        _active = cover;

        (bool Applied, long Milliseconds) result;
        try
        {
            result = await ApplyTimedAsync(apply).ConfigureAwait(true);
        }
        catch
        {
            _active = null;
            cover.Cancel();
            throw;
        }

        if (!result.Applied)
        {
            // The desktop still shows what the cover is painted with. Leaving it up to
            // fade would be a fade to the very same picture, which reads as a flicker.
            // Forgotten here and not just cancelled: taking it down is a posted
            // message, so it stays alive for a moment longer and the next change must
            // not be handed to a cover that is on its way out.
            _active = null;
            cover.Cancel();
            return false;
        }

        cover.BeginFade(GetHold(result.Milliseconds));
        return true;
    }

    /// <summary>
    /// Takes down a fade that is still running and waits for its thread to let go of
    /// Explorer's window. For shutdown: the new wallpaper is already on the desktop
    /// underneath, so this leaves the right picture on screen, just without the fade.
    /// </summary>
    public static void Cancel()
    {
        Cover? cover = _active;
        _active = null;
        cover?.CancelAndJoin();
    }

    /// <summary>The fade in flight, or null once the last one has finished.</summary>
    private static Cover? GetRunning()
    {
        if (_active is null)
        {
            return null;
        }

        if (_active.IsRunning)
        {
            return _active;
        }

        _active = null;
        return null;
    }

    /// <summary>Runs the apply and reports how long it took, which sets the hold.</summary>
    private static async Task<(bool Applied, long Milliseconds)> ApplyTimedAsync(Func<Task<bool>> apply)
    {
        Stopwatch clock = Stopwatch.StartNew();
        bool applied = await apply().ConfigureAwait(true);
        return (applied, clock.ElapsedMilliseconds);
    }

    private static int GetHold(long spent) =>
        spent < MinHoldMilliseconds
            ? MinHoldMilliseconds
            : (spent > MaxHoldMilliseconds ? MaxHoldMilliseconds : (int)spent);

    /// <summary>
    /// Finds the window the wallpaper is painted in, which is the one to parent the
    /// cover into.
    ///
    /// <para>
    /// After the undocumented Progman message the desktop is two windows: one holding
    /// SHELLDLL_DefView (the icons) and a WorkerW right behind it in the z order that
    /// carries the wallpaper. That WorkerW is what we want. When it cannot be found -
    /// a shell replacement, a future Explorer, a desktop tool that rearranged the
    /// layer - Progman itself still paints the wallpaper and is the right answer, and
    /// the caller puts the cover at the bottom of the z order so the icons stay above
    /// it either way.
    /// </para>
    /// </summary>
    private static IntPtr FindWallpaperHost()
    {
        IntPtr progman = NativeMethods.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        IntPtr worker = FindWorkerBehindIcons();
        if (worker == IntPtr.Zero)
        {
            // Look before asking, so the undocumented message is off the common path:
            // the split outlives the request and only has to be made once per Explorer.
            NativeMethods.SendMessageTimeoutW(
                progman,
                NativeMethods.WM_PROGMAN_SPAWN_WORKERW,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.SMTO_ABORTIFHUNG,
                1000,
                out _);

            worker = FindWorkerBehindIcons();
        }

        if (worker == IntPtr.Zero)
        {
            Logger.Debug("fade: no workerw behind the icons, using progman");
            return progman;
        }

        return worker;
    }

    /// <summary>
    /// The WorkerW that sits right behind the window holding the desktop icons, or
    /// zero while the desktop has not been split into the two.
    /// </summary>
    private static IntPtr FindWorkerBehindIcons()
    {
        IntPtr worker = IntPtr.Zero;

        bool Visit(IntPtr hWnd, IntPtr param)
        {
            if (NativeMethods.FindWindowExW(hWnd, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
            {
                return true;
            }

            // The sibling *after* the icon host in the z order, i.e. the one behind it.
            worker = NativeMethods.FindWindowExW(IntPtr.Zero, hWnd, "WorkerW", null);
            return worker == IntPtr.Zero;
        }

        NativeMethods.EnumWindowsProc callback = Visit;
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return worker;
    }

    /// <summary>
    /// The window that holds the outgoing picture, the thread it lives on, and the
    /// timer that fades it out.
    ///
    /// <para>
    /// A NativeWindow rather than a Form: this is a child of another process's window
    /// with no chrome, no input and three messages to answer, and a Form would bring a
    /// control tree and a lifetime model that have nothing to do with any of that.
    /// </para>
    /// <para>
    /// Everything below runs on the fade thread except the members the caller drives
    /// it with - <see cref="ShowAsync"/>, <see cref="HoldAsync"/>,
    /// <see cref="IsRunning"/>, <see cref="BeginFade"/> and <see cref="Cancel"/> -
    /// which cross threads through tasks, one volatile flag and posted messages, and
    /// nothing else.
    /// </para>
    /// </summary>
    private sealed class Cover : NativeWindow
    {
        private readonly Thread _thread;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch _clock = new Stopwatch();

        /// <summary>
        /// Completed by the fade thread once the cover is up, or once it is certain it
        /// will not be. Continuations run off the fade thread so that a caller waiting
        /// on it cannot end up running on the thread that has a fade to draw.
        /// </summary>
        private readonly TaskCompletionSource<bool> _ready =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Completed when the fade thread ends, whatever ended it. The backstop under
        /// every wait on this cover: a caller can be sure it is answered, because this
        /// one is set in a finally rather than by a message that has to be delivered.
        /// </summary>
        private readonly TaskCompletionSource<bool> _completed =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Callers waiting for <see cref="WM_FADE_HOLD"/> to be carried out.</summary>
        private readonly ConcurrentQueue<TaskCompletionSource<bool>> _holds =
            new ConcurrentQueue<TaskCompletionSource<bool>>();

        private ApplicationContext? _loop;
        private IntPtr _host;
        private Size _size;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _replacedBitmap;
        private int _hold;
        private int _ticks;
        private int _relays;
        private int _folds;
        private bool _fading;

        /// <summary>
        /// Whether the cover is currently hiding the desktop completely, which is the
        /// condition for changing the wallpaper without it being watched. True from the
        /// moment it goes up until the first tick that lowers the alpha, and again
        /// after every successful <see cref="Fold"/>.
        /// </summary>
        private bool _opaque = true;

        /// <summary>Set by the fade thread when its message loop has ended.</summary>
        private volatile bool _finished;

        public Cover()
        {
            _timer.Interval = TickMilliseconds;
            _timer.Tick += OnTick;

            _thread = new Thread(Run)
            {
                // Background, so a fade can never be the reason the process stays
                // alive; it is half a second of animation and nothing is lost by
                // dropping it. STA because it hosts windows.
                IsBackground = true,
                Name = "wallpaper fade",
            };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        /// <summary>Whether the fade thread is still there to be handed a change.</summary>
        public bool IsRunning => !_finished;

        /// <summary>
        /// Starts the fade thread and reports whether the cover made it onto the
        /// desktop. Awaited rather than waited on: the caller's own message loop keeps
        /// running while this happens, and the wallpaper must not be applied until it
        /// has an answer either way.
        /// </summary>
        public async Task<bool> ShowAsync()
        {
            _thread.Start();

            Task<bool> ready = _ready.Task;
            Task first = await Task
                .WhenAny(ready, Task.Delay(ShowTimeoutMilliseconds))
                .ConfigureAwait(true);

            if (!ReferenceEquals(first, ready))
            {
                Logger.Warn("fade: the cover did not come up in time, cutting instead");

                // It may still come up after this. Take it down the moment it does:
                // a cover nobody is going to fade would leave the picture it was
                // painted with frozen on top of the wallpaper that replaced it.
                _ = ready.ContinueWith(
                    (task, state) => ((Cover)state).Cancel(),
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);

                return false;
            }

            return await ready.ConfigureAwait(true);
        }

        /// <summary>
        /// Makes the cover opaque again, so the wallpaper can be changed underneath it
        /// unseen, and reports when that has actually happened.
        ///
        /// <para>
        /// Answered rather than fired and forgotten, because the order matters: what
        /// goes opaque is a picture of the desktop as it is *now*, and taking it after
        /// the new wallpaper is up would fold in the very thing being hidden.
        /// </para>
        /// <para>
        /// The wait is safe by construction rather than by timeout: it ends either on
        /// the answer or on the fade thread ending, and the latter is signalled from a
        /// finally, so a message that never gets delivered cannot leave anyone hanging.
        /// </para>
        /// </summary>
        public Task HoldAsync()
        {
            TaskCompletionSource<bool> pending =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _holds.Enqueue(pending);
            Post(WM_FADE_HOLD, 0);
            return Task.WhenAny(pending.Task, _completed.Task);
        }

        /// <summary>
        /// Tells the cover the wallpaper underneath it has changed, and it may start
        /// fading after <paramref name="hold"/>. Safe to call more than once: see the
        /// handler in <see cref="WndProc"/>.
        /// </summary>
        public void BeginFade(int hold) => Post(WM_FADE_BEGIN, hold);

        /// <summary>Takes the cover down without waiting for it to be gone.</summary>
        public void Cancel() => Post(WM_FADE_CANCEL, 0);

        /// <summary>
        /// Takes the cover down and waits for its thread to end, which is what releases
        /// the input queue attachment to Explorer. The wait is bounded because the
        /// thread it waits on is parented into another process's window: a wedged
        /// Explorer must not be able to hold up this program's shutdown.
        /// </summary>
        public void CancelAndJoin()
        {
            Cancel();
            if (!_thread.Join(JoinMilliseconds))
            {
                Logger.Warn("fade: the fade thread did not end in time, leaving it to the process exit");
            }
        }

        private void Post(int message, int parameter)
        {
            IntPtr handle = Handle;
            if (handle == IntPtr.Zero || _finished)
            {
                return;
            }

            NativeMethods.PostMessageW(handle, (uint)message, new IntPtr(parameter), IntPtr.Zero);
        }

        /// <summary>
        /// The fade thread from end to end: build the cover, pump its messages until
        /// the fade is over, then give every handle back.
        /// </summary>
        private void Run()
        {
            try
            {
                bool built = Build();
                _ready.TrySetResult(built);
                if (!built)
                {
                    return;
                }

                // Nothing else is ever queued to this thread, which is the point: a
                // WM_TIMER is the lowest priority message there is, and on the main
                // thread a burst of clicks starves it for the whole length of a fade.
                _loop = new ApplicationContext();
                Application.Run(_loop);
            }
            catch (Exception ex)
            {
                Logger.Warn("fade: the fade thread failed error=" + ex.Message);
                _ready.TrySetResult(false);
            }
            finally
            {
                // First, so that a caller asking whether this cover can still take a
                // change gets "no" for the whole of the teardown rather than only
                // after it.
                _finished = true;

                Destroy();
                ReleaseWaiters();
            }
        }

        /// <summary>Ends every wait on this cover. Called once, from a finally.</summary>
        private void ReleaseWaiters()
        {
            while (_holds.TryDequeue(out TaskCompletionSource<bool>? pending))
            {
                pending.TrySetResult(false);
            }

            _ready.TrySetResult(false);
            _completed.TrySetResult(true);
        }

        /// <summary>
        /// Builds the cover and puts it up, opaque. False means this machine's desktop
        /// is not the shape this needs, and the change should cut.
        /// </summary>
        private bool Build()
        {
            IntPtr previousContext = IntPtr.Zero;
            try
            {
                // The process is system DPI aware (see app.manifest), which would report
                // the monitors and size this window in the primary monitor's scale - the
                // wrong pixel grid on a second monitor scaled differently. The wallpaper
                // layer is physical pixels, so this thread is made per-monitor aware;
                // nothing else in the process changes, and this one only lives for the
                // fade anyway.
                previousContext = NativeMethods.SetThreadDpiAwarenessContext(
                    NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

                IntPtr host = FindWallpaperHost();
                if (host == IntPtr.Zero)
                {
                    Logger.Warn("fade: no desktop wallpaper window, cutting instead");
                    return false;
                }

                if (!NativeMethods.GetWindowRect(host, out NativeMethods.RECT hostRect))
                {
                    Logger.Warn("fade: the wallpaper window has no rectangle, cutting instead");
                    return false;
                }

                // The desktop window has no frame, so its client origin is its window
                // origin and a child at 0,0 covers exactly the virtual screen it spans.
                Rectangle bounds = Rectangle.FromLTRB(
                    hostRect.Left, hostRect.Top, hostRect.Right, hostRect.Bottom);
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    Logger.Warn("fade: the wallpaper window is empty, cutting instead");
                    return false;
                }

                _size = bounds.Size;
                CreateSurface();

                if (!Capture(host))
                {
                    return false;
                }

                Show(host);
                _host = host;

                if (Logger.IsEnabled(LogLevel.Debug))
                {
                    Logger.Debug(
                        // IntPtr does not implement IFormattable on .NET Framework, so
                        // the handle goes through Int64 to be formatted at all.
                        "fade: covered host=0x" + host.ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                        " size=" + bounds.Width.ToString(CultureInfo.InvariantCulture) +
                        "x" + bounds.Height.ToString(CultureInfo.InvariantCulture));
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn("fade: preparing the crossfade failed, cutting instead error=" + ex.Message);
                return false;
            }
            finally
            {
                if (previousContext != IntPtr.Zero)
                {
                    // No try needed: a non-zero value means the first call resolved and
                    // succeeded, so this one cannot fail to find the entry point either.
                    NativeMethods.SetThreadDpiAwarenessContext(previousContext);
                }
            }
        }

        /// <summary>
        /// Allocates the screen compatible bitmap the cover is painted from.
        ///
        /// <para>
        /// Compatible with the screen rather than a GDI+ Bitmap on purpose. This is
        /// the only copy of the frame that ever exists - a Bitmap would need a second
        /// one to hand GDI a HBITMAP to blit from, and at a UHD desktop that copy is
        /// tens of megabytes. Painting is then a single BitBlt, and so is filling it.
        /// </para>
        /// </summary>
        private void CreateSurface()
        {
            IntPtr screen = NativeMethods.GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero)
            {
                throw new InvalidOperationException("GetDC for the screen failed.");
            }

            try
            {
                _memoryDc = NativeMethods.CreateCompatibleDC(screen);
                if (_memoryDc == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateCompatibleDC failed.");
                }

                _bitmap = NativeMethods.CreateCompatibleBitmap(screen, _size.Width, _size.Height);
                if (_bitmap == IntPtr.Zero)
                {
                    throw new InvalidOperationException("CreateCompatibleBitmap failed.");
                }

                _replacedBitmap = NativeMethods.SelectObject(_memoryDc, _bitmap);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>
        /// Copies the wallpaper layer's own pixels into the surface, and reports
        /// whether anything came back.
        ///
        /// <para>
        /// This is everything the cover needs and it is already composed: the right
        /// fit, the right monitor layout, the right resampling, for one blit inside
        /// video memory instead of a UHD JPEG read, decoded and rescaled on the UI
        /// thread. The host is a top level window with a redirection surface of its
        /// own, so what comes back is the wallpaper alone - the icons live in a
        /// sibling window and every real window is composed above both.
        /// </para>
        /// <para>
        /// Undocumented all the same, and DWM is free to hand back a blank surface for
        /// a window it composes another way, without saying so. There is no way to ask
        /// in advance, so the result is sampled instead: a surface that is one flat
        /// colour is called a failure and the change cuts. That rule misjudges a
        /// genuinely single coloured wallpaper, which costs it a fade nobody could
        /// have seen anyway.
        /// </para>
        /// <para>
        /// Every failure here is logged at Warn, not Debug: this is the only way the
        /// cover is ever painted, so a machine where it does not work is a machine
        /// with no crossfade at all, and that should not need Debug logging to find.
        /// </para>
        /// </summary>
        private bool Capture(IntPtr host)
        {
            IntPtr hostDc = NativeMethods.GetDC(host);
            if (hostDc == IntPtr.Zero)
            {
                Logger.Warn("fade: the wallpaper window has no device context, cutting instead");
                return false;
            }

            bool copied;
            try
            {
                copied = NativeMethods.BitBlt(
                    _memoryDc, 0, 0, _size.Width, _size.Height, hostDc, 0, 0, NativeMethods.SRCCOPY);
            }
            finally
            {
                NativeMethods.ReleaseDC(host, hostDc);
            }

            if (!copied)
            {
                Logger.Warn("fade: copying the wallpaper layer failed, cutting instead");
                return false;
            }

            if (!HasDetail())
            {
                Logger.Warn("fade: the wallpaper layer came back flat, cutting instead");
                return false;
            }

            return true;
        }

        /// <summary>Puts the cover up, opaque, underneath the desktop icons.</summary>
        private void Show(IntPtr host)
        {
            CreateParams parameters = new CreateParams
            {
                Caption = "BingWallpaper wallpaper fade",
                Parent = host,
                X = 0,
                Y = 0,
                Width = _size.Width,
                Height = _size.Height,
                Style = NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE | NativeMethods.WS_DISABLED,
                ExStyle = NativeMethods.WS_EX_LAYERED
                    | NativeMethods.WS_EX_TRANSPARENT
                    | NativeMethods.WS_EX_NOACTIVATE
                    | NativeMethods.WS_EX_NOPARENTNOTIFY,
            };

            CreateHandle(parameters);

            // Below the icon window when the parent turned out to be Progman; harmless
            // when it is the WorkerW, where there are no siblings to be below.
            NativeMethods.SetWindowPos(
                Handle,
                NativeMethods.HWND_BOTTOM,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

            // A layered window shows nothing until its alpha has been set once. Paint
            // it right afterwards instead of waiting for the message loop's turn: the
            // wallpaper is applied next, and it has to happen out of sight.
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);
            NativeMethods.UpdateWindow(Handle);

            // Painted is not the same as on screen: DWM presents on its own cycle, and
            // the wallpaper is swapped the moment this returns. Without waiting out one
            // cycle there is a frame in which the new picture is up and the cover that
            // is supposed to be hiding it is not - which is the cut, with extra steps.
            NativeMethods.DwmFlush();
        }

        /// <summary>
        /// Ends the message loop, which sends <see cref="Run"/> on to release the
        /// window and the bitmap.
        /// </summary>
        private void Finish()
        {
            _timer.Stop();
            _loop?.ExitThread();
        }

        /// <summary>Releases everything the fade thread owns. Runs on that thread.</summary>
        private void Destroy()
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _clock.Stop();

            if (_fading && Logger.IsEnabled(LogLevel.Debug))
            {
                // ticks is the whole diagnosis when a fade did not appear on screen:
                // the alpha follows the clock, so one single tick means the timer was
                // starved for the entire fade and the only tick to arrive found it
                // already over. relays counts the changes this one cover absorbed.
                Logger.Debug(
                    "fade: done ticks=" + _ticks.ToString(CultureInfo.InvariantCulture) +
                    " elapsed=" + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " hold=" + _hold.ToString(CultureInfo.InvariantCulture) +
                    " relays=" + _relays.ToString(CultureInfo.InvariantCulture) +
                    " folds=" + _folds.ToString(CultureInfo.InvariantCulture));
            }

            // The window first: it is the only thing that paints out of the device
            // context below, and destroying it drops any paint still queued for it.
            try
            {
                DestroyHandle();
            }
            catch (Exception ex)
            {
                Logger.Warn("fade: destroying the cover window failed error=" + ex.Message);
            }

            if (_memoryDc != IntPtr.Zero)
            {
                if (_replacedBitmap != IntPtr.Zero)
                {
                    NativeMethods.SelectObject(_memoryDc, _replacedBitmap);
                    _replacedBitmap = IntPtr.Zero;
                }

                NativeMethods.DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_ERASEBKGND:
                    // Every pixel is painted below, so erasing first only costs a
                    // full screen fill of the wrong colour.
                    m.Result = new IntPtr(1);
                    return;

                case NativeMethods.WM_PAINT:
                {
                    NativeMethods.PAINTSTRUCT paint = default;
                    IntPtr hdc = NativeMethods.BeginPaint(m.HWnd, ref paint);
                    if (hdc != IntPtr.Zero)
                    {
                        NativeMethods.BitBlt(
                            hdc, 0, 0, _size.Width, _size.Height, _memoryDc, 0, 0, NativeMethods.SRCCOPY);
                        NativeMethods.EndPaint(m.HWnd, ref paint);
                    }

                    m.Result = IntPtr.Zero;
                    return;
                }

                case WM_FADE_HOLD:
                    try
                    {
                        Fold();
                    }
                    finally
                    {
                        if (_holds.TryDequeue(out TaskCompletionSource<bool>? pending))
                        {
                            pending.TrySetResult(true);
                        }
                    }

                    return;

                case WM_FADE_BEGIN:
                    OnBeginFade(m.WParam.ToInt32());
                    return;

                case WM_FADE_CANCEL:
                    Finish();
                    return;
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Pauses the fade with the cover opaque, having first folded whatever is on
        /// screen into the cover's own picture so that nothing appears to change.
        ///
        /// <para>
        /// Halfway through a fade the desktop shows the cover's picture at some alpha
        /// over the wallpaper behind it. Blending that wallpaper into the cover by the
        /// complementary alpha makes the cover hold exactly the image that was being
        /// composed - so raising the alpha back to opaque replaces a composition with
        /// an identical picture and not one pixel moves. The wallpaper can then be
        /// changed underneath unseen and the fade picks up from its own midpoint,
        /// which is why a click during a fade reads as one continuous transition from
        /// the first picture to the last instead of a cut to the new one.
        /// </para>
        /// <para>
        /// There is nothing to fold while the cover is still opaque, which is the
        /// common case - clicks usually arrive during the hold. The clock is stopped
        /// either way: the alpha must not move while the caller changes the wallpaper,
        /// and an apply on a slow machine easily outlasts the hold it interrupted.
        /// </para>
        /// </summary>
        private void Fold()
        {
            if (!_fading)
            {
                // Not started, so it is opaque and standing still already.
                return;
            }

            _timer.Stop();

            if (_opaque || _host == IntPtr.Zero)
            {
                return;
            }

            byte alpha = GetAlpha(_clock.ElapsedMilliseconds - _hold);

            IntPtr hostDc = NativeMethods.GetDC(_host);
            if (hostDc == IntPtr.Zero)
            {
                // The cover keeps the picture it had. Going opaque from here is a jump
                // back to the outgoing picture, so leave the alpha where it is and let
                // the change show - the same cut this used to be, and no worse.
                Logger.Debug("fade: the wallpaper window has no device context, not folding");
                return;
            }

            try
            {
                NativeMethods.BLENDFUNCTION blend = new NativeMethods.BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    SourceConstantAlpha = (byte)(255 - alpha),
                };

                if (!NativeMethods.AlphaBlend(
                        _memoryDc, 0, 0, _size.Width, _size.Height,
                        hostDc, 0, 0, _size.Width, _size.Height,
                        blend))
                {
                    Logger.Debug("fade: folding the wallpaper into the cover failed");
                    return;
                }
            }
            catch (Exception ex)
            {
                // msimg32 is a system library, but this is the only call into it.
                Logger.Warn("fade: alphablend is not usable, letting the change show error=" + ex.Message);
                return;
            }
            finally
            {
                NativeMethods.ReleaseDC(_host, hostDc);
            }

            // Paint before raising the alpha, not after: the wrong order would put the
            // outgoing picture back up at full strength for as long as it takes to
            // repaint. This order can at worst show one frame that is slightly too far
            // towards the new picture, and only if DWM happens to compose between the
            // two calls.
            NativeMethods.InvalidateRect(Handle, IntPtr.Zero, false);
            NativeMethods.UpdateWindow(Handle);
            NativeMethods.SetLayeredWindowAttributes(Handle, 0, 255, NativeMethods.LWA_ALPHA);

            _opaque = true;
            _folds++;
        }

        /// <summary>
        /// Starts the fade, or puts the hold back to the beginning when a change lands
        /// on a cover that is already up.
        ///
        /// <para>
        /// Restarting is what makes a burst of clicks read as one transition: every
        /// change is applied while the cover hides the desktop - either because the
        /// fade had not started yet or because <see cref="Fold"/> just made it opaque
        /// again - and the fade that eventually runs ends on the last picture chosen.
        /// </para>
        /// </summary>
        private void OnBeginFade(int hold)
        {
            if (!_fading)
            {
                _fading = true;
                _hold = hold;
                _clock.Start();
                _timer.Start();
                return;
            }

            _relays++;

            if (!_opaque)
            {
                // The fold could not happen, so the cover is part way through and
                // raising it back to opaque would put the outgoing picture on screen
                // again - a flash, and a worse one than the change it would hide. The
                // fade already running is let finish instead, which shows the change:
                // the cut this used to be, on a path that is now the exception.
                _timer.Start();
                return;
            }

            _hold = hold;
            _clock.Restart();
            _timer.Start();
        }

        /// <summary>The alpha a fade this far along should be showing.</summary>
        private static byte GetAlpha(long elapsedSinceHold)
        {
            double progress = elapsedSinceHold / (double)FadeMilliseconds;
            if (progress >= 1.0)
            {
                return 0;
            }

            // Smoothstep rather than a straight ramp: a linear alpha starts and stops
            // with a visible edge, and the ends are exactly the moments a wallpaper
            // change is being looked at.
            double eased = progress * progress * (3.0 - (2.0 * progress));
            return (byte)(255.0 - (eased * 255.0));
        }

        /// <summary>
        /// Whether the captured surface holds more than one colour, sampled on a three
        /// by three grid inset from the edges. Cheap, and the only question worth
        /// asking: a copy that DWM refused comes back as one flat colour, usually
        /// black, and a photograph never does.
        /// </summary>
        private bool HasDetail()
        {
            uint first = NativeMethods.CLR_INVALID;

            for (int row = 1; row <= 3; row++)
            {
                for (int column = 1; column <= 3; column++)
                {
                    uint colour = NativeMethods.GetPixel(
                        _memoryDc, _size.Width * column / 4, _size.Height * row / 4);
                    if (colour == NativeMethods.CLR_INVALID)
                    {
                        return false;
                    }

                    if (first == NativeMethods.CLR_INVALID)
                    {
                        first = colour;
                    }
                    else if (colour != first)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                _ticks++;

                long elapsed = _clock.ElapsedMilliseconds;
                if (elapsed < _hold)
                {
                    return;
                }

                if (elapsed - _hold >= FadeMilliseconds)
                {
                    Finish();
                    return;
                }

                byte alpha = GetAlpha(elapsed - _hold);
                NativeMethods.SetLayeredWindowAttributes(Handle, 0, alpha, NativeMethods.LWA_ALPHA);
                _opaque = alpha == 255;
            }
            catch (Exception ex)
            {
                // The new wallpaper is already underneath, so ending here is a cut.
                Logger.Warn("fade: stopped early error=" + ex.Message);
                Finish();
            }
        }
    }
}
