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
        "Chrome/139.0.0.0 Safari/537.36 Edg/139.0.0.0";

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
        // .NET Framework defaults to the OS protocol list since 4.7, but a stale
        // machine-wide setting would break TLS 1.2 - make sure it is enabled.
        try
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }
        catch (NotSupportedException ex)
        {
            Logger.Warn("Could not enable TLS 1.2 explicitly: " + ex.Message);
        }

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
        string url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}?format=js&idx={1}&n={2}&uhd=1&setmkt={3}&ensearch=1",
            ApiBase,
            safeIdx,
            safeCount,
            Uri.EscapeDataString(market));

        Logger.Info("API request: " + url);

        string json = await RunWithRetryAsync(
            async () =>
            {
                using (HttpResponseMessage response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    Logger.Info("API response: HTTP " + (int)response.StatusCode + " " + response.StatusCode);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            },
            "fetch image metadata",
            cancellationToken).ConfigureAwait(false);

        List<BingImageInfo> images = ParseImages(json);
        Logger.Info("API returned " + images.Count + " image(s).");
        foreach (BingImageInfo image in images)
        {
            Logger.Info(
                "  startdate=" + image.StartDate +
                " fullstartdate=" + image.FullStartDate +
                " wp=" + image.Wp +
                " title=" + image.DisplayTitle);

            if (!image.Wp)
            {
                Logger.Warn(
                    "Image " + image.StartDate + " is flagged wp=false (Bing does not mark it as " +
                    "downloadable wallpaper). Continuing anyway - this is a personal tool.");
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
                Logger.Warn("Skipping response entry without urlbase/startdate.");
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
        Logger.Info("Downloading " + url);
        Logger.Info("  target: " + destinationPath);

        long bytes = await RunWithRetryAsync(
            async () =>
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                DeleteQuietly(tempPath);

                using (HttpResponseMessage response = await _http
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    Logger.Info("  HTTP " + (int)response.StatusCode + " " + response.StatusCode);
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

                Logger.Info(
                    "  downloaded " + length + " bytes in " + stopwatch.ElapsedMilliseconds +
                    " ms, decoded " + width + "x" + height);

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

    /// <summary>Decodes the file to prove the bytes really are an image.</summary>
    public static bool TryValidateImage(string path, out int width, out int height, out string? error)
    {
        width = 0;
        height = 0;
        error = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (Image image = Image.FromStream(
                stream,
                useEmbeddedColorManagement: false,
                validateImageData: true))
            {
                width = image.Width;
                height = image.Height;
                return width > 0 && height > 0;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
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
                    "Attempt " + (attempt + 1) + " to " + what + " failed (" + ex.GetType().Name + ": " +
                    ex.Message + "). Retrying in " + delay.TotalSeconds + "s.");
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
            Logger.Warn("Could not delete " + path + ": " + ex.Message);
        }
    }
}
