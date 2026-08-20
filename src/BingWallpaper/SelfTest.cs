using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BingWallpaper;

/// <summary>
/// Head-less end-to-end check: fetch metadata, build the image URL, download the
/// image and decode it. Never sets a wallpaper, never creates a window and never
/// writes the configuration file. Exit code 0 = success, 1 = failure.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(string[] args)
    {
        Logger.EchoToConsole = true;

        string market = "zh-CN";
        ResolutionKind resolution = ResolutionKind.Uhd;

        // The configuration file is read when present, but never created or modified.
        if (File.Exists(Paths.ConfigFile))
        {
            AppConfig config = AppConfig.Load(Paths.ConfigFile);
            market = config.Market;
            resolution = config.Resolution;
        }

        foreach (string arg in args)
        {
            if (arg.StartsWith("--market=", StringComparison.OrdinalIgnoreCase))
            {
                market = AppConfig.NormalizeMarket(arg.Substring("--market=".Length));
            }
            else if (arg.StartsWith("--resolution=", StringComparison.OrdinalIgnoreCase))
            {
                string value = arg.Substring("--resolution=".Length).Trim().ToLowerInvariant();
                resolution = value is "1080p" or "1920x1080" or "fullhd"
                    ? ResolutionKind.FullHd
                    : ResolutionKind.Uhd;
            }
        }

        Logger.Info("=== BingWallpaper self test ===");
        Program.LogEnvironment();
        Logger.Info("Market: " + market + "  Resolution: " + AppConfig.ResolutionToString(resolution));

        string tempFile = Path.Combine(
            Path.GetTempPath(),
            "BingWallpaper-selftest-" + Guid.NewGuid().ToString("N") + ".jpg");

        try
        {
            using (BingClient client = new BingClient())
            using (CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromMinutes(3)))
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                List<BingImageInfo> images = await client
                    .FetchAsync(market, 0, BingClient.MaxImageCount, cts.Token)
                    .ConfigureAwait(false);

                BingImageInfo today = images[0];
                string imageUrl = today.GetImageUrl(resolution);
                Logger.Info("[ OK ] metadata: " + images.Count + " entries, newest startdate=" + today.StartDate);
                Logger.Info("[ OK ] title: " + today.DisplayTitle);
                Logger.Info("[ OK ] copyright: " + today.Copyright);
                Logger.Info("[ OK ] image url: " + imageUrl);
                Logger.Info("[ OK ] cache file name would be: " + today.GetFileName(market, resolution));

                long bytes = await client.DownloadImageAsync(imageUrl, tempFile, cts.Token).ConfigureAwait(false);

                int width;
                int height;
                string? error;
                if (!BingClient.TryValidateImage(tempFile, out width, out height, out error))
                {
                    Logger.Error("[FAIL] downloaded file did not decode: " + error);
                    return 1;
                }

                stopwatch.Stop();
                Logger.Info(
                    "[ OK ] downloaded " + bytes.ToString(CultureInfo.InvariantCulture) + " bytes, decoded " +
                    width + "x" + height);

                if (resolution == ResolutionKind.Uhd && width < 3000)
                {
                    Logger.Warn(
                        "UHD was requested but the decoded image is only " + width + "px wide. " +
                        "Bing may not offer a 4K variant for this market/day.");
                }

                Logger.Info("=== self test PASSED in " + stopwatch.ElapsedMilliseconds + " ms ===");
                return 0;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[FAIL] self test failed.", ex);
            Logger.Info("=== self test FAILED ===");
            return 1;
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not delete the temporary self test file: " + ex.Message);
            }
        }
    }
}
