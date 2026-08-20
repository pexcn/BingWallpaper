using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BingWallpaper;

/// <summary>
/// One entry of the HPImageArchive response.
/// The identity of an image is <see cref="StartDate"/> - never the local date,
/// because markets roll over at their own UTC hour (zh-CN rolls at 16:00 UTC).
/// </summary>
internal sealed class BingImageInfo
{
    public const string BingHost = "https://www.bing.com";

    private const string IdMarker = "OHR.";

    private const string FallbackImageId = "image";

    /// <summary>Real tokens are far shorter; this only guards against a pathological urlbase.</summary>
    private const int MaxImageIdLength = 64;

    /// <summary>yyyyMMdd, e.g. "20260818".</summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>yyyyMMddHHmm, e.g. "202608181600".</summary>
    public string FullStartDate { get; set; } = string.Empty;

    public string EndDate { get; set; } = string.Empty;

    /// <summary>e.g. "/th?id=OHR.WhyteCliffP_ZH-CN0573407830" - the suffix is ours to choose.</summary>
    public string UrlBase { get; set; } = string.Empty;

    /// <summary>
    /// Stable identity of the photo, taken from the "OHR.{name}" token of
    /// <see cref="UrlBase"/> - "WhyteCliffP" for the example above. The same photo
    /// carries the same token in every market; only the locale suffix differs.
    /// Naming cache files after it is what keeps a picture that Bing publishes in
    /// seven markets on the same day from being stored seven times.
    /// </summary>
    public string ImageId => ExtractImageId(UrlBase);

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

    /// <summary>
    /// {startdate}_{imageId}_{UHD|1920x1080}.jpg
    /// The market is deliberately absent: it describes the channel the image was
    /// fetched through, not the image itself. Including it used to store one copy
    /// per market of what is a single photo.
    /// </summary>
    public string GetFileName(ResolutionKind resolution)
        => StartDate + "_" + ImageId + "_" + AppConfig.ResolutionToString(resolution) + ".jpg";

    /// <summary>
    /// The inverse of <see cref="GetFileName"/>. Needed when a picture has aged out
    /// of the eight day window and its file name is the only metadata left: the id
    /// never contains an underscore (see <see cref="Sanitize"/>), so the three
    /// segments can always be told apart again.
    /// </summary>
    public static bool TryParseFileName(string fileName, out string startDate, out string imageId)
    {
        startDate = string.Empty;
        imageId = string.Empty;
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        int first = name.IndexOf('_');
        int last = name.LastIndexOf('_');
        if (first <= 0 || last <= first)
        {
            return false;
        }

        string date = name.Substring(0, first);
        if (date.Length != 8 || !IsAllDigits(date))
        {
            return false;
        }

        startDate = date;
        imageId = name.Substring(first + 1, last - first - 1);
        return true;
    }

    /// <summary>
    /// Pulls the market independent part out of a urlbase. Falls back to whatever
    /// follows the last "=" when the "OHR." marker is missing, and to a constant
    /// when nothing usable is left - a cache file has to have a name either way.
    /// </summary>
    public static string ExtractImageId(string urlBase)
    {
        if (string.IsNullOrEmpty(urlBase))
        {
            return FallbackImageId;
        }

        int start = urlBase.IndexOf(IdMarker, StringComparison.OrdinalIgnoreCase);
        start = start >= 0 ? start + IdMarker.Length : urlBase.LastIndexOf('=') + 1;

        string id = urlBase.Substring(start);

        // "WhyteCliffP_ZH-CN0573407830" -> "WhyteCliffP". The locale and the serial
        // are what makes the same photo look different from market to market.
        int suffix = id.LastIndexOf('_');
        if (suffix > 0)
        {
            id = id.Substring(0, suffix);
        }

        return Sanitize(id);
    }

    /// <summary>
    /// Reduces the token to ASCII letters, digits and dashes. Underscores are
    /// dropped as well so that the three segments of a cache file name stay
    /// unambiguous when split apart again.
    /// </summary>
    private static string Sanitize(string raw)
    {
        StringBuilder sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c == '-'))
            {
                sb.Append(c);
            }

            if (sb.Length == MaxImageIdLength)
            {
                break;
            }
        }

        return sb.Length > 0 ? sb.ToString() : FallbackImageId;
    }

    /// <summary>StartDate rendered as yyyy-MM-dd, or the raw value when unparsable.</summary>
    public string DisplayDate => FormatDate(StartDate);

    /// <summary>
    /// Formats a yyyyMMdd token for display. Static because a pinned picture that
    /// has left the eight day window has a date but no <see cref="BingImageInfo"/>
    /// to hang it on.
    /// </summary>
    public static string FormatDate(string startDate)
    {
        if (DateTime.TryParseExact(
                startDate,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return startDate;
    }

    /// <summary>
    /// char.IsDigit would also accept Arabic-Indic and other Unicode digits, which
    /// DateTime.TryParseExact then rejects.
    /// </summary>
    private static bool IsAllDigits(string value)
    {
        foreach (char c in value)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Copyright : Title;

    /// <summary>
    /// Date and title on one line, for the places that show both. The middle dot
    /// separates them; two spaces read as a gap the layout forgot to close.
    /// </summary>
    public string DisplayLine => DisplayDate + " · " + DisplayTitle;

    public override string ToString() => DisplayDate + " " + DisplayTitle;
}
