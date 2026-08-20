using System;
using System.Globalization;

namespace BingWallpaper;

/// <summary>
/// One entry of the HPImageArchive response.
/// The identity of an image is <see cref="StartDate"/> - never the local date,
/// because markets roll over at their own UTC hour (zh-CN rolls at 16:00 UTC).
/// </summary>
internal sealed class BingImageInfo
{
    public const string BingHost = "https://www.bing.com";

    /// <summary>yyyyMMdd, e.g. "20260818".</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>yyyyMMddHHmm, e.g. "202608181600".</summary>
    public string FullStartDate { get; set; } = string.Empty;

    public string EndDate { get; set; } = string.Empty;

    /// <summary>e.g. "/th?id=OHR.WhyteCliffP_ZH-CN0573407830" - the suffix is ours to choose.</summary>
    public string UrlBase { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Copyright { get; set; } = string.Empty;

    public string CopyrightLink { get; set; } = string.Empty;

    /// <summary>Whether Bing marks the image as allowed to be used as a wallpaper.</summary>
    public bool Wp { get; set; } = true;

    /// <summary>
    /// Builds the download URL from <see cref="UrlBase"/>. The "url" field of the
    /// response is deliberately ignored: the server appends &amp;w=1920&amp;h=1080 to it,
    /// so the resolution has to be controlled entirely on the client side.
    /// </summary>
    public string GetImageUrl(ResolutionKind resolution)
        => BingHost + UrlBase + (resolution == ResolutionKind.Uhd ? "_UHD.jpg" : "_1920x1080.jpg");

    /// <summary>Small variant used for the history grid thumbnails.</summary>
    public string GetThumbnailUrl() => BingHost + UrlBase + "_400x240.jpg";

    /// <summary>{market}_{startdate}_{UHD|1920x1080}.jpg</summary>
    public string GetFileName(string market, ResolutionKind resolution)
        => market + "_" + StartDate + "_" + AppConfig.ResolutionToString(resolution) + ".jpg";

    /// <summary>StartDate rendered as yyyy-MM-dd, or the raw value when unparsable.</summary>
    public string DisplayDate
    {
        get
        {
            if (DateTime.TryParseExact(
                    StartDate,
                    "yyyyMMdd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return StartDate;
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Copyright : Title;

    public override string ToString() => DisplayDate + " " + DisplayTitle;
}
