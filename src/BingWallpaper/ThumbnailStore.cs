using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;

namespace BingWallpaper;

/// <summary>
/// Thumbnails of the local pictures: a disk cache under wallpapers\.thumbs\, a single
/// background worker that fills it, and the decoded bitmaps of what is on screen.
///
/// <para>
/// Not to be confused with <see cref="ThumbnailCache"/>, which holds the small JPEGs
/// Bing serves for the last eight days. Nothing here comes off the network: a
/// favourite may be years old, so its thumbnail can only be made from the picture on
/// disk - and making one means decoding a UHD JPEG, which is the reason for
/// everything below.
/// </para>
/// <para>
/// One worker, not a pool. The first pass over a picture allocates GDI+'s full size
/// intermediate bitmap (3840 x 2160 x 4 bytes is 33 MB), so a second thread would
/// double the memory peak without halving the wall clock - the work is decode bound,
/// not latency bound.
/// </para>
/// <para>
/// The pending list is *replaced* rather than appended to: every scroll hands over the
/// range that is on screen now, and whatever was queued for a range the user has
/// already scrolled past is dropped on the spot. That is what keeps a fast scroll from
/// spending the next minute rendering the five hundred pictures nobody is looking at
/// any more.
/// </para>
/// </summary>
internal sealed class ThumbnailStore : IDisposable
{
    /// <summary>
    /// Long edge of what is written to disk, in logical pixels. Larger than the tile
    /// so the cache survives a bigger tile or a denser screen; small enough to stay
    /// around 25 KB per picture at quality 85.
    /// </summary>
    public const int DiskEdge = 320;

    private const long JpegQuality = 85L;

    private readonly object _sync = new object();
    private readonly List<string> _pending = new List<string>();
    private readonly AutoResetEvent _signal = new AutoResetEvent(false);

    /// <summary>UI thread only. A null value marks a picture that would not decode.</summary>
    private readonly Dictionary<string, Bitmap?> _bitmaps = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);

    private readonly ISynchronizeInvoke _synchronizer;
    private readonly Action<string, Bitmap?> _publish;
    private readonly int _diskEdge;
    private readonly int _tileWidth;

    private Thread? _worker;
    private HashSet<string>? _sweep;

    // Volatile: the worker loops on it, and it is set from the UI thread.
    private volatile bool _disposed;

    /// <param name="synchronizer">The control results are handed back on.</param>
    /// <param name="tileWidth">Width of the box that paints a thumbnail, in device pixels.</param>
    public ThumbnailStore(ISynchronizeInvoke synchronizer, int tileWidth)
    {
        _synchronizer = synchronizer;
        _tileWidth = Math.Max(1, tileWidth);
        _diskEdge = Math.Max(_tileWidth, DpiScale.Round(DiskEdge));
        _publish = OnRendered;
    }

    /// <summary>Raised on the UI thread once a picture is ready (or known to be broken).</summary>
    public event Action<string>? Ready;

    /// <summary>
    /// Declares which pictures are worth holding: the visible range plus a screen on
    /// either side. Everything outside is disposed, so the bitmaps in memory are bound
    /// by the size of the window and not by the size of the collection.
    /// </summary>
    public void SetWindow(IReadOnlyList<string> names)
    {
        if (_disposed)
        {
            return;
        }

        HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<string>? work = null;
        for (int i = 0; i < names.Count; i++)
        {
            keep.Add(names[i]);
            if (!_bitmaps.ContainsKey(names[i]))
            {
                (work ??= new List<string>()).Add(names[i]);
            }
        }

        Trim(keep);

        lock (_sync)
        {
            _pending.Clear();
            if (work is not null)
            {
                _pending.AddRange(work);
            }
        }

        if (work is not null)
        {
            EnsureWorker();
            _signal.Set();
        }
    }

    /// <summary>
    /// The bitmap of <paramref name="fileName"/>, if it has been rendered.
    /// <paramref name="failed"/> tells a picture that will never arrive from one that
    /// has not arrived yet - the first gets a placeholder, the second gets "loading".
    /// </summary>
    public Bitmap? Get(string fileName, out bool failed)
    {
        if (_bitmaps.TryGetValue(fileName, out Bitmap? bitmap))
        {
            failed = bitmap is null;
            return bitmap;
        }

        failed = false;
        return null;
    }

    /// <summary>
    /// Asks for cache files whose picture is gone to be deleted, next time the worker
    /// has nothing better to do. Lazy and optional: an orphan costs 25 KB and nothing
    /// else, and this is the only place in the program that deletes under wallpapers\
    /// besides the retention pass.
    /// </summary>
    public void RequestSweep(IReadOnlyList<string> liveNames)
    {
        HashSet<string> live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < liveNames.Count; i++)
        {
            live.Add(liveNames[i] + ".jpg");
        }

        lock (_sync)
        {
            _sweep = live;
        }

        EnsureWorker();
        _signal.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Not joined: the worker may be halfway through decoding a 4K picture, and
        // there is nothing it holds that has to be released in order - it is a
        // background thread, so it cannot keep the process alive either. The event is
        // not disposed for the same reason: the worker may be sitting in WaitOne, and
        // the ObjectDisposedException that would raise has no one to catch it.
        _signal.Set();

        foreach (Bitmap? bitmap in _bitmaps.Values)
        {
            bitmap?.Dispose();
        }

        _bitmaps.Clear();
    }

    /// <summary>
    /// Scales a picture so that its longer edge is at most <paramref name="maxEdge"/>,
    /// in one high quality step. Successive halving was tried for the icon frames and
    /// rejected there for the same reason: it comes out soft.
    /// <para>
    /// Also the only way to get a bitmap that does not depend on the stream it was
    /// decoded from - Image.FromStream keeps reading from it - so the copy may as well
    /// be the size that is actually going to be painted.
    /// </para>
    /// </summary>
    public static Bitmap Scale(Image source, int maxEdge)
    {
        int longest = Math.Max(source.Width, source.Height);
        if (longest <= 0 || maxEdge <= 0 || longest <= maxEdge)
        {
            return new Bitmap(source);
        }

        int width = Math.Max(1, (int)Math.Round((double)source.Width * maxEdge / longest));
        int height = Math.Max(1, (int)Math.Round((double)source.Height * maxEdge / longest));

        Bitmap scaled = new Bitmap(width, height);
        try
        {
            using (Graphics g = Graphics.FromImage(scaled))
            using (ImageAttributes attributes = new ImageAttributes())
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                // A bicubic kernel samples past the edge of the source, and what it
                // finds there is the transparent background of a fresh bitmap, which
                // comes out as a half transparent border. Mirroring gives the filter
                // real pixels to work with.
                attributes.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(
                    source,
                    new Rectangle(0, 0, width, height),
                    0,
                    0,
                    source.Width,
                    source.Height,
                    GraphicsUnit.Pixel,
                    attributes);
            }

            return scaled;
        }
        catch
        {
            scaled.Dispose();
            throw;
        }
    }

    /// <summary>Decodes bytes into a bitmap no larger than <paramref name="maxEdge"/>.</summary>
    public static Bitmap Decode(byte[] bytes, int maxEdge)
    {
        using (MemoryStream stream = new MemoryStream(bytes))
        using (Image source = Image.FromStream(stream))
        {
            return Scale(source, maxEdge);
        }
    }

    /// <summary>Disposes every bitmap that is not in <paramref name="keep"/>.</summary>
    private void Trim(HashSet<string> keep)
    {
        List<string>? stale = null;
        foreach (KeyValuePair<string, Bitmap?> entry in _bitmaps)
        {
            if (!keep.Contains(entry.Key))
            {
                (stale ??= new List<string>()).Add(entry.Key);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (string name in stale)
        {
            _bitmaps[name]?.Dispose();
            _bitmaps.Remove(name);
        }
    }

    private void EnsureWorker()
    {
        if (_worker is not null || _disposed)
        {
            return;
        }

        _worker = new Thread(Work)
        {
            IsBackground = true,
            Name = "thumbnails",
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    private void Work()
    {
        while (!_disposed)
        {
            _signal.WaitOne();

            while (!_disposed)
            {
                string? name = TakeNext();
                if (name is null)
                {
                    break;
                }

                Bitmap? bitmap = null;
                try
                {
                    bitmap = Render(name);
                }
                catch (Exception ex)
                {
                    // A truncated download, a renamed non-picture, a file the user
                    // pulled away mid-scroll: the tile gets a placeholder and the list
                    // carries on. Only pictures somebody actually scrolled to pay this.
                    Logger.Warn("thumbnail: rendering failed file=" + name + " error=" + ex.Message);
                }

                Publish(name, bitmap);
            }

            // Only once the visible range is served: this is housekeeping, and the
            // pictures on screen come first.
            HashSet<string>? sweep = TakeSweep();
            if (sweep is not null && !_disposed)
            {
                Sweep(sweep);
            }
        }
    }

    private string? TakeNext()
    {
        lock (_sync)
        {
            if (_pending.Count == 0)
            {
                return null;
            }

            // From the front: the list is handed over in the order the tiles appear,
            // so the top of the viewport fills in first.
            string name = _pending[0];
            _pending.RemoveAt(0);
            return name;
        }
    }

    private HashSet<string>? TakeSweep()
    {
        lock (_sync)
        {
            HashSet<string>? sweep = _sweep;
            _sweep = null;
            return sweep;
        }
    }

    /// <summary>
    /// The thumbnail of one picture, from the disk cache when it is still current and
    /// from the picture itself otherwise. A cache entry older than its source is
    /// stale - which is what makes overwriting a favourite with a new picture of the
    /// same name show the new one.
    /// </summary>
    private Bitmap Render(string fileName)
    {
        string source = Path.Combine(Paths.FavoritesDirectory, fileName);
        string cache = Path.Combine(Paths.ThumbnailDirectory, fileName + ".jpg");

        if (IsCacheCurrent(cache, source))
        {
            try
            {
                return Decode(File.ReadAllBytes(cache), _tileWidth);
            }
            catch (Exception ex)
            {
                // A cache entry that will not decode is ours to replace.
                Logger.Debug("thumbnail: cache entry unreadable file=" + fileName + " error=" + ex.Message);
            }
        }

        using (FileStream stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (Image picture = Image.FromStream(stream))
        {
            // Two scalings from one decode rather than one scaling and a rescale of the
            // result: the expensive part is the decode, and a thumbnail resampled from
            // another thumbnail is visibly softer.
            using (Bitmap forDisk = Scale(picture, _diskEdge))
            {
                WriteCache(cache, forDisk);
            }

            return Scale(picture, _tileWidth);
        }
    }

    private static bool IsCacheCurrent(string cache, string source)
    {
        try
        {
            return File.Exists(cache) && File.GetLastWriteTimeUtc(cache) >= File.GetLastWriteTimeUtc(source);
        }
        catch (Exception ex)
        {
            Logger.Debug("thumbnail: checking the cache entry failed file=" + Path.GetFileName(cache) + " error=" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Writes one cache entry. Failing to cache is not failing to show a thumbnail, so
    /// this never throws - the picture is already rendered by the time it is called.
    /// </summary>
    private static void WriteCache(string path, Bitmap thumbnail)
    {
        string tmp = path + ".tmp";
        try
        {
            Paths.EnsureThumbnailDirectory();

            ImageCodecInfo? codec = JpegCodec;
            if (codec is null)
            {
                return;
            }

            using (EncoderParameters parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                thumbnail.Save(tmp, codec, parameters);
            }

            // Renamed rather than written in place: a half written entry is newer than
            // its source and would therefore be trusted from then on.
            Paths.MoveOverwrite(tmp, path);
        }
        catch (Exception ex)
        {
            Logger.Debug("thumbnail: writing the cache entry failed file=" + Path.GetFileName(path) + " error=" + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug("thumbnail: removing a temporary cache entry failed error=" + ex.Message);
            }
        }
    }

    private static void Sweep(HashSet<string> live)
    {
        int deleted = 0;
        try
        {
            if (!Directory.Exists(Paths.ThumbnailDirectory))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(Paths.ThumbnailDirectory, "*.jpg", SearchOption.TopDirectoryOnly))
            {
                if (live.Contains(Path.GetFileName(path)))
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    Logger.Debug("thumbnail: deleting an orphan failed path=" + path + " error=" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("thumbnail: sweeping the cache failed error=" + ex.Message);
            return;
        }

        if (deleted > 0)
        {
            Logger.Info("thumbnail: swept orphans removed=" + deleted.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Hands a finished bitmap back to the UI thread, which owns the dictionary.</summary>
    private void Publish(string name, Bitmap? bitmap)
    {
        try
        {
            if (_disposed)
            {
                bitmap?.Dispose();
                return;
            }

            _synchronizer.BeginInvoke(_publish, new object?[] { name, bitmap });
        }
        catch (Exception ex)
        {
            // The window went away between the decode and the hand off.
            bitmap?.Dispose();
            Logger.Debug("thumbnail: publishing failed file=" + name + " error=" + ex.Message);
        }
    }

    private void OnRendered(string name, Bitmap? bitmap)
    {
        if (_disposed)
        {
            bitmap?.Dispose();
            return;
        }

        if (_bitmaps.TryGetValue(name, out Bitmap? existing))
        {
            existing?.Dispose();
        }

        _bitmaps[name] = bitmap;
        Ready?.Invoke(name);
    }

    /// <summary>
    /// The JPEG encoder, looked up once. GDI+ answers Save(path, codec, parameters)
    /// with a codec it was given and nothing else, so there is no quality control
    /// without it.
    /// </summary>
    private static ImageCodecInfo? JpegCodec { get; } = FindJpegCodec();

    private static ImageCodecInfo? FindJpegCodec()
    {
        try
        {
            foreach (ImageCodecInfo codec in ImageCodecInfo.GetImageEncoders())
            {
                if (string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    return codec;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("thumbnail: looking up the jpeg encoder failed error=" + ex.Message);
        }

        Logger.Warn("thumbnail: no jpeg encoder, the disk cache is disabled");
        return null;
    }
}
