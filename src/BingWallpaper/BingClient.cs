using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace BingWallpaper;

/// <summary>
/// Talks to https://www.bing.com/HPImageArchive.aspx and downloads images.
/// Only the endpoint verified in the specification is used - no multi endpoint
/// fallback, on purpose.
/// </summary>
internal sealed class BingClient : IDisposable
{
    /// <summary>Bing only serves the last 8 days: idx 0..7, n max 8.</summary>
    public const int MaxImageCount = 8;

    private const string ApiBase = "https://www.bing.com/HPImageArchive.aspx";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/151.0.0.0 Safari/537.36";

    private static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
    };

    private readonly HttpClient _http;
    private bool _disposed;

    public BingClient()
    {
        // ServicePointManager.SecurityProtocol is deliberately left alone. Because the
        // project targets .NET Framework 4.7 or later its default is SystemDefault,
        // which lets SChannel negotiate the highest protocol the OS offers - TLS 1.3
        // included. Assigning an explicit value (even "|= Tls12") opts out of that and
        // would pin the client to the listed protocols forever.

        HttpClientHandler handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
        };

        if (handler.SupportsAutomaticDecompression)
        {
            handler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        }

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
    }

    /// <summary>
    /// Fetches image metadata. <paramref name="idx"/> must be 0..7 and
    /// <paramref name="count"/> 1..8; both are clamped.
    /// </summary>
    public async Task<List<BingImageInfo>> FetchAsync(
        string market,
        int idx,
        int count,
        CancellationToken cancellationToken)
    {
        int safeIdx = Clamp(idx, 0, MaxImageCount - 1);
        int safeCount = Clamp(count, 1, MaxImageCount);

        // No ensearch=1. That flag forces the English channel, which makes setmkt
        // decorative: zh-CN then answers with the en-US titles and the en-US picture
        // set, so the market a user picked had no effect on anything they could see.
        string url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}?format=js&idx={1}&n={2}&uhd=1&setmkt={3}",
            ApiBase,
            safeIdx,
            safeCount,
            Uri.EscapeDataString(market));

        Logger.Info("api: request url=" + url);

        string json = await RunWithRetryAsync(
            async () =>
            {
                using (HttpResponseMessage response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    Logger.Debug("api: response status=" + (int)response.StatusCode + " " + response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            },
            "fetch image metadata",
            cancellationToken).ConfigureAwait(false);

        List<BingImageInfo> images = ParseImages(json);

        // One Info line per response; the per-image breakdown is Debug because eight
        // titles on every refresh cycle would dominate the rotation window.
        Logger.Info(
            "api: images=" + images.Count +
            " latest=" + (images.Count > 0 ? images[0].StartDate : "none"));

        bool debug = Logger.IsEnabled(LogLevel.Debug);
        foreach (BingImageInfo image in images)
        {
            if (debug)
            {
                Logger.Debug(
                    "api: image startdate=" + image.StartDate +
                    " fullstartdate=" + image.FullStartDate +
                    " wp=" + image.Wp +
                    " title=" + image.DisplayTitle);
            }

            if (!image.Wp)
            {
                // Bing does not mark this one as a downloadable wallpaper. Used anyway:
                // this is a personal tool and the flag has been wrong before.
                Logger.Warn("api: image not flagged as wallpaper startdate=" + image.StartDate + " wp=false");
            }
        }

        return images;
    }

    /// <summary>
    /// Parses the HPImageArchive JSON payload. JavaScriptSerializer ships with
    /// .NET Framework (System.Web.Extensions), so this stays dependency free -
    /// System.Text.Json is not available here.
    /// </summary>
    public static List<BingImageInfo> ParseImages(string json)
    {
        List<BingImageInfo> result = new List<BingImageInfo>();

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        object? root = serializer.DeserializeObject(json);
        if (!(root is Dictionary<string, object> map)
            || !map.TryGetValue("images", out object imagesValue)
            || !(imagesValue is object[] images))
        {
            throw new InvalidDataException("Response does not contain an \"images\" array.");
        }

        foreach (object item in images)
        {
            if (!(item is Dictionary<string, object> entry))
            {
                continue;
            }

            string urlBase = GetString(entry, "urlbase");
            string startDate = GetString(entry, "startdate");
            if (string.IsNullOrEmpty(urlBase) || string.IsNullOrEmpty(startDate))
            {
                Logger.Warn("api: skipping entry without urlbase/startdate");
                continue;
            }

            result.Add(new BingImageInfo
            {
                StartDate = startDate,
                FullStartDate = GetString(entry, "fullstartdate"),
                EndDate = GetString(entry, "enddate"),
                UrlBase = urlBase,
                Title = GetString(entry, "title"),
                Copyright = GetString(entry, "copyright"),
                CopyrightLink = GetString(entry, "copyrightlink"),
                Wp = GetBool(entry, "wp", true),
            });
        }

        if (result.Count == 0)
        {
            throw new InvalidDataException("Response contained no usable image entries.");
        }

        return result;
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destinationPath"/>.
    /// The bytes land in a ".tmp" file first and are only renamed after the image
    /// decodes, so a truncated download can never be mistaken for a valid cache entry.
    /// </summary>
    public async Task<long> DownloadImageAsync(
        string url,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        string tempPath = destinationPath + ".tmp";
        Logger.Debug("download: start url=" + url + " target=" + destinationPath);

        long bytes = await RunWithRetryAsync(
            async () =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                DeleteQuietly(tempPath);

                using (HttpResponseMessage response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    Logger.Debug("download: response status=" + (int)response.StatusCode + " " + response.StatusCode);
                    response.EnsureSuccessStatusCode();

                    using (Stream source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream target = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        useAsync: true))
                    {
                        await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
                    }
                }

                stopwatch.Stop();
                long length = new FileInfo(tempPath).Length;

                int width;
                int height;
                string? error;
                if (!TryValidateImage(tempPath, out width, out height, out error))
                {
                    DeleteQuietly(tempPath);
                    throw new InvalidDataException("Downloaded file is not a decodable image: " + error);
                }

                // Single Info line for the whole transfer: the url, the target and the
                // outcome belong together, and separate lines drift apart once other
                // threads interleave.
                Logger.Info(
                    "download: done url=" + url +
                    " target=" + destinationPath +
                    " bytes=" + length +
                    " ms=" + stopwatch.ElapsedMilliseconds +
                    " decoded=" + width + "x" + height);

                Paths.MoveOverwrite(tempPath, destinationPath);
                return length;
            },
            "download image",
            cancellationToken).ConfigureAwait(false);

        return bytes;
    }

    /// <summary>Downloads a small resource fully into memory (history thumbnails).</summary>
    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken cancellationToken)
    {
        return await RunWithRetryAsync(
            async () =>
            {
                using (HttpResponseMessage response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            },
            "download thumbnail",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the header to prove the bytes really are an image, and checks the end of
    /// the file to prove the download was not cut short.
    ///
    /// <para>
    /// validateImageData is deliberately false. Passing true makes GDI+ decode every
    /// pixel, and for a UHD wallpaper that is a 3840x2160x4 bitmap - over 30 MB - held
    /// for the length of the call, which is by far the largest allocation this program
    /// makes and it exists only to answer a yes or no question. With false, GDI+ parses
    /// the header, which is enough for both the format check and the dimensions.
    /// </para>
    /// <para>
    /// The one thing the header cannot catch is a truncated body, which is exactly the
    /// failure this validation exists for - a connection dropped mid transfer leaves a
    /// file that still opens. <see cref="HasCompleteTrailer"/> covers that instead, at
    /// the cost of reading the last two bytes.
    /// </para>
    /// </summary>
    public static bool TryValidateImage(string path, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                using (Image image = Image.FromStream(
                    stream,
                    useEmbeddedColorManagement: false,
                    validateImageData: false))
                {
                    width = image.Width;
                    height = image.Height;
                    if (width <= 0 || height <= 0)
                    {
                        error = "the header reports a " + width + "x" + height + " image";
                        return false;
                    }
                }

                if (!HasCompleteTrailer(stream, out error))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Whether the file ends the way its format says it should - the cheap stand in for
    /// decoding the whole picture just to notice a download stopped halfway.
    ///
    /// <para>
    /// Only JPEG and PNG are checked because those are the only two Bing serves. An
    /// unrecognised format is accepted rather than rejected: this is a truncation guard,
    /// and the format itself was already vouched for by the decoder above.
    /// </para>
    /// </summary>
    private static bool HasCompleteTrailer(FileStream stream, out string? error)
    {
        error = null;
        if (stream.Length < 4)
        {
            error = "the file is only " + stream.Length + " bytes long";
            return false;
        }

        byte[] head = new byte[2];
        stream.Position = 0;
        if (stream.Read(head, 0, 2) != 2)
        {
            error = "the file is too short to identify";
            return false;
        }

        // JPEG: SOI FF D8, and a complete stream ends with EOI FF D9.
        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            byte[] tail = new byte[2];
            stream.Position = stream.Length - 2;
            if (stream.Read(tail, 0, 2) != 2 || tail[0] != 0xFF || tail[1] != 0xD9)
            {
                error = "the JPEG data ends without an EOI marker (truncated download)";
                return false;
            }

            return true;
        }

        // PNG: signature starts 89 50, and the last chunk of a complete file is IEND,
        // whose type field sits 8 bytes before the end (4 type + 4 CRC).
        if (head[0] == 0x89 && head[1] == 0x50)
        {
            if (stream.Length < 12)
            {
                error = "the PNG data is too short to hold an IEND chunk";
                return false;
            }

            byte[] tail = new byte[4];
            stream.Position = stream.Length - 8;
            if (stream.Read(tail, 0, 4) != 4
                || tail[0] != 'I' || tail[1] != 'E' || tail[2] != 'N' || tail[3] != 'D')
            {
                error = "the PNG data ends without an IEND chunk (truncated download)";
                return false;
            }

            return true;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _http.Dispose();
    }

    /// <summary>Math.Clamp does not exist on .NET Framework.</summary>
    private static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    private static async Task<T> RunWithRetryAsync<T>(
        Func<Task<T>> operation,
        string what,
        CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (int attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt == RetryDelays.Length)
                {
                    break;
                }

                TimeSpan delay = RetryDelays[attempt];
                Logger.Warn(
                    "retry: op=\"" + what + "\" attempt=" + (attempt + 1) +
                    " error=" + ex.GetType().Name + ": " + ex.Message +
                    " retryin=" + delay.TotalSeconds + "s");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException(
            "Failed to " + what + " after " + (RetryDelays.Length + 1) + " attempts.",
            last);
    }

    private static string GetString(Dictionary<string, object> entry, string name)
        => entry.TryGetValue(name, out object value) && value is string text ? text : string.Empty;

    private static bool GetBool(Dictionary<string, object> entry, string name, bool fallback)
    {
        if (!entry.TryGetValue(name, out object value))
        {
            return fallback;
        }

        if (value is bool flag)
        {
            return flag;
        }

        if (value is int number)
        {
            return number != 0;
        }

        return fallback;
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("cleanup: delete failed path=" + path + " error=" + ex.Message);
        }
    }
}
