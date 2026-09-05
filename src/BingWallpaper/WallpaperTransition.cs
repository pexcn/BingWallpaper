using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
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
/// The alpha is the only thing that moves: the frame is uploaded once and DWM
/// composes it, so a tick costs one byte rather than a full screen blend.
/// </para>
/// <para>
/// Two costs are worth knowing about. A window parented across a process boundary
/// attaches the two input queues for as long as it lives, so a wedged Explorer
/// could wedge this program - the cover exists for about half a second, which is
/// what makes that acceptable. And the frame is a screen sized bitmap next to the
/// decoded picture, tens of megabytes on a 4K desktop, which is why both are
/// released the moment the fade ends rather than kept for the next one.
/// </para>
/// <para>
/// None of this is documented by Microsoft, so every step is allowed to fail: no
/// Progman, no WorkerW, no device context, a picture that will not decode - each
/// one logs and falls back to the plain cut, which is what the program did before.
/// </para>
/// </summary>
internal static class WallpaperTransition
{
    /// <summary>
    /// How long the cover stays opaque before the fade starts.
    /// <para>
    /// SystemParametersInfoW returns once it has transcoded the picture, but Explorer
    /// paints it on its own thread afterwards and says nothing when it is done.
    /// Dropping the alpha before that paint lands would show the *old* wallpaper
    /// through the fade - the one thing this is supposed to hide.
    /// </para>
    /// </summary>
    private const int HoldMilliseconds = 120;

    /// <summary>How long the fade itself takes.</summary>
    private const int FadeMilliseconds = 380;

    /// <summary>
    /// Roughly 60 steps a second. The elapsed time drives the alpha, not the tick
    /// count, so a late tick costs smoothness and never correctness.
    /// </summary>
    private const int TickMilliseconds = 15;

    private static Cover? _active;

    /// <summary>
    /// Applies a wallpaper under a crossfade from <paramref name="previousPath"/>.
    /// <para>
    /// <paramref name="apply"/> is always called exactly once, whether or not the
    /// cover could be built, and its result is passed straight back: a caller cannot
    /// tell the difference between a faded change and a cut, and should not have to.
    /// </para>
    /// </summary>
    public static bool Run(string previousPath, Func<bool> apply)
    {
        Cancel();

        Cover? cover = TryCover(previousPath);
        if (cover is null)
        {
            return apply();
        }

        _active = cover;

        bool applied;
        try
        {
            applied = apply();
        }
        catch
        {
            cover.Dispose();
            throw;
        }

        if (!applied)
        {
            // The desktop still shows what the cover is painted with. Leaving it up to
            // fade would be a fade to the very same picture, which reads as a flicker.
            cover.Dispose();
            return false;
        }

        cover.Start();
        return true;
    }

    /// <summary>
    /// Ends a fade that is still running. The new wallpaper is already on the desktop
    /// underneath, so this leaves the right picture on screen, just without the fade.
    /// </summary>
    public static void Cancel() => _active?.Dispose();

    /// <summary>
    /// Builds and shows the cover, or returns null when this machine's desktop is not
    /// the shape this needs. Every failure here is a reason to cut, never to fail.
    /// </summary>
    private static Cover? TryCover(string previousPath)
    {
        if (!Application.MessageLoop)
        {
            // The fade owns a window and a WinForms timer, both of which belong to the
            // thread that pumps messages. Every caller is on it; this guards a future
            // one that is not.
            Logger.Debug("fade: skipped, the calling thread has no message loop");
            return null;
        }

        IntPtr previousContext = IntPtr.Zero;
        Cover? cover = null;
        try
        {
            // The process is system DPI aware (see app.manifest), which would report
            // the monitors and size this window in the primary monitor's scale - the
            // wrong pixel grid on a second monitor scaled differently. The wallpaper
            // layer is physical pixels, so this one window is measured and created as
            // per-monitor aware; nothing else in the process changes.
            previousContext = NativeMethods.SetThreadDpiAwarenessContext(
                NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            IntPtr host = FindWallpaperHost();
            if (host == IntPtr.Zero)
            {
                Logger.Warn("fade: no desktop wallpaper window, cutting instead");
                return null;
            }

            if (!NativeMethods.GetWindowRect(host, out NativeMethods.RECT hostRect))
            {
                Logger.Warn("fade: the wallpaper window has no rectangle, cutting instead");
                return null;
            }

            // The desktop window has no frame, so its client origin is its window
            // origin and a child at 0,0 covers exactly the virtual screen it spans.
            Rectangle bounds = Rectangle.FromLTRB(hostRect.Left, hostRect.Top, hostRect.Right, hostRect.Bottom);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Logger.Warn("fade: the wallpaper window is empty, cutting instead");
                return null;
            }

            List<Rectangle> monitors = GetMonitors(bounds);
            cover = new Cover(bounds.Size);
            cover.Render(previousPath, bounds, monitors);
            cover.Show(host);

            if (Logger.IsEnabled(LogLevel.Debug))
            {
                Logger.Debug(
                    // IntPtr does not implement IFormattable on .NET Framework, so the
                    // handle goes through Int64 to be formatted at all.
                    "fade: covered host=0x" + host.ToInt64().ToString("X", CultureInfo.InvariantCulture) +
                    " size=" + bounds.Width.ToString(CultureInfo.InvariantCulture) +
                    "x" + bounds.Height.ToString(CultureInfo.InvariantCulture) +
                    " monitors=" + monitors.Count.ToString(CultureInfo.InvariantCulture) +
                    " from=" + Path.GetFileName(previousPath));
            }

            return cover;
        }
        catch (Exception ex)
        {
            Logger.Warn("fade: preparing the crossfade failed, cutting instead error=" + ex.Message);
            cover?.Dispose();
            return null;
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
    /// The monitor rectangles, in virtual screen coordinates. Fill is a per monitor
    /// rule, so the frame is drawn one monitor at a time rather than once across the
    /// whole desktop.
    /// </summary>
    private static List<Rectangle> GetMonitors(Rectangle fallback)
    {
        List<Rectangle> monitors = new List<Rectangle>(2);

        bool Visit(IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr param)
        {
            monitors.Add(Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            return true;
        }

        NativeMethods.MonitorEnumProc callback = Visit;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        if (monitors.Count == 0)
        {
            // One picture stretched over the whole desktop is not what Windows will
            // draw on a multi monitor setup, but this only happens when the monitors
            // could not be listed at all, and a single monitor is the usual case.
            Logger.Warn("fade: could not list the monitors, covering the desktop as one");
            monitors.Add(fallback);
        }

        return monitors;
    }

    /// <summary>
    /// Where a picture lands under Fill (WallpaperStyle 10): scaled to cover the
    /// target, aspect ratio kept, centred, whatever sticks out cropped away.
    /// </summary>
    private static Rectangle GetFillRectangle(Rectangle target, Size image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return target;
        }

        double scale = Math.Max(
            target.Width / (double)image.Width,
            target.Height / (double)image.Height);
        int width = (int)Math.Round(image.Width * scale);
        int height = (int)Math.Round(image.Height * scale);

        return new Rectangle(
            target.X + ((target.Width - width) / 2),
            target.Y + ((target.Height - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// The window that holds the outgoing picture, and the timer that fades it out.
    ///
    /// <para>
    /// A NativeWindow rather than a Form: this is a child of another process's window
    /// with no chrome, no input and one message to answer, and a Form would bring a
    /// control tree and a lifetime model that have nothing to do with any of that.
    /// </para>
    /// </summary>
    private sealed class Cover : NativeWindow, IDisposable
    {
        private readonly Size _size;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer();
        private readonly Stopwatch _clock = new Stopwatch();

        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _replacedBitmap;
        private bool _disposed;

        public Cover(Size size)
        {
            _size = size;
            _timer.Interval = TickMilliseconds;
            _timer.Tick += OnTick;
        }

        /// <summary>
        /// Draws the outgoing picture into a screen compatible bitmap.
        ///
        /// <para>
        /// Compatible with the screen rather than a GDI+ Bitmap on purpose. This is
        /// the only copy of the frame that ever exists - a Bitmap would need a second
        /// one to hand GDI a HBITMAP to blit from, and at a UHD desktop that copy is
        /// tens of megabytes. Painting is then a single BitBlt.
        /// </para>
        /// </summary>
        public void Render(string path, Rectangle bounds, List<Rectangle> monitors)
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

            // Read the file rather than decode from it: Image.FromStream keeps reading
            // from the stream for as long as the image lives, and this picture is the
            // wallpaper - a locked file is the one thing it must not become.
            byte[] bytes = File.ReadAllBytes(path);
            using (MemoryStream stream = new MemoryStream(bytes, writable: false))
            using (Image source = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false))
            using (Graphics graphics = Graphics.FromHdc(_memoryDc))
            {
                // The cover has to match what Windows drew a moment ago closely enough
                // that putting it up is invisible, so this is not the place to save a
                // few milliseconds on resampling.
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                foreach (Rectangle monitor in monitors)
                {
                    Rectangle target = new Rectangle(
                        monitor.X - bounds.X,
                        monitor.Y - bounds.Y,
                        monitor.Width,
                        monitor.Height);

                    // SetClip rather than assigning Clip: the property takes a Region,
                    // which would be one more GDI object to own and release per monitor.
                    graphics.SetClip(target);
                    graphics.DrawImage(source, GetFillRectangle(target, source.Size));
                }
            }
        }

        /// <summary>Puts the cover up, opaque, underneath the desktop icons.</summary>
        public void Show(IntPtr host)
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
                    | NativeMethods.WS_EX_NOACTIVATE,
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

        public void Start()
        {
            _clock.Start();
            _timer.Start();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer.Dispose();
            _clock.Stop();

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

            if (ReferenceEquals(_active, this))
            {
                _active = null;
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
            }

            base.WndProc(ref m);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                long elapsed = _clock.ElapsedMilliseconds;
                if (elapsed < HoldMilliseconds)
                {
                    return;
                }

                double progress = (elapsed - HoldMilliseconds) / (double)FadeMilliseconds;
                if (progress >= 1.0)
                {
                    Dispose();
                    return;
                }

                // Smoothstep rather than a straight ramp: a linear alpha starts and
                // stops with a visible edge, and the ends are exactly the moments a
                // wallpaper change is being looked at.
                double eased = progress * progress * (3.0 - (2.0 * progress));
                byte alpha = (byte)(255.0 - (eased * 255.0));
                NativeMethods.SetLayeredWindowAttributes(Handle, 0, alpha, NativeMethods.LWA_ALPHA);
            }
            catch (Exception ex)
            {
                // The new wallpaper is already underneath, so ending here is a cut.
                Logger.Warn("fade: stopped early error=" + ex.Message);
                Dispose();
            }
        }
    }
}
