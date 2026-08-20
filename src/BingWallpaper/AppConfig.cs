using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BingWallpaper;

internal enum ResolutionKind
{
    /// <summary>urlbase + "_UHD.jpg"</summary>
    Uhd,

    /// <summary>urlbase + "_1920x1080.jpg"</summary>
    FullHd,
}

internal enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Tile,
    Center,
    Span,
}

internal enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// INI backed configuration. Plain text on purpose: the user is expected to be
/// able to edit it by hand, and INI matches the portable-app convention.
/// </summary>
internal sealed class AppConfig
{
    public const string SectionName = "General";

    public const int MinRefreshIntervalHours = 1;
    public const int MaxRefreshIntervalHours = 168;
    public const int MaxKeepDays = 3650;

    public string Market { get; set; } = "zh-CN";

    public ResolutionKind Resolution { get; set; } = ResolutionKind.Uhd;

    public WallpaperFit Fit { get; set; } = WallpaperFit.Fill;

    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public int RefreshIntervalHours { get; set; } = 1;

    /// <summary>0 means "keep forever".</summary>
    public int KeepDays { get; set; } = 30;

    public bool RunAtStartup { get; set; }

    /// <summary>
    /// File name of the wallpaper the user pinned, or an empty string when the
    /// wallpaper follows the refresh timer. Deliberately a bare file name and not a
    /// path: the picture keeps its identity wherever the wallpaper folder ends up
    /// being organised, and a hand edited path could point anywhere.
    /// </summary>
    public string PinnedWallpaper { get; set; } = string.Empty;

    /// <summary>Whether the wallpaper is currently held against the refresh timer.</summary>
    public bool IsPinned => PinnedWallpaper.Length > 0;

    /// <summary>
    /// Reads the configuration file. A missing or damaged file yields defaults and
    /// is never rewritten implicitly - only explicit Save() touches the disk.
    /// </summary>
    public static AppConfig Load(string path)
    {
        AppConfig config = new AppConfig();
        Dictionary<string, string> values = ReadIni(path);
        if (values.Count == 0)
        {
            return config;
        }

        config.Market = NormalizeMarket(GetString(values, "Market", config.Market));
        config.Resolution = ParseResolution(GetString(values, "Resolution", "UHD"));
        config.Fit = ParseEnum(GetString(values, "Fit", "Fill"), WallpaperFit.Fill);
        config.Theme = ParseEnum(GetString(values, "Theme", "System"), ThemeMode.System);
        config.RefreshIntervalHours = Clamp(
            GetInt(values, "RefreshIntervalHours", 1),
            MinRefreshIntervalHours,
            MaxRefreshIntervalHours);
        config.KeepDays = Clamp(GetInt(values, "KeepDays", 30), 0, MaxKeepDays);
        config.RunAtStartup = GetBool(values, "RunAtStartup", false);
        config.PinnedWallpaper = SanitizeFileName(GetString(values, "PinnedWallpaper", string.Empty));
        return config;
    }

    public void Save(string path)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("[" + SectionName + "]");
        sb.AppendLine("Market=" + Market);
        sb.AppendLine("Resolution=" + ResolutionToString(Resolution));
        sb.AppendLine("Fit=" + Fit);
        sb.AppendLine("Theme=" + Theme);
        sb.AppendLine("RefreshIntervalHours=" + RefreshIntervalHours.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("KeepDays=" + KeepDays.ToString(CultureInfo.InvariantCulture));
        sb.AppendLine("RunAtStartup=" + (RunAtStartup ? "true" : "false"));
        sb.AppendLine("PinnedWallpaper=" + PinnedWallpaper);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Paths.MoveOverwrite(tmp, path);
    }

    public static string ResolutionToString(ResolutionKind kind) => kind == ResolutionKind.Uhd ? "UHD" : "1920x1080";

    /// <summary>Normalizes a market code to the "xx-YY" shape Bing expects.</summary>
    public static string NormalizeMarket(string? market)
    {
        if (string.IsNullOrWhiteSpace(market))
        {
            return "zh-CN";
        }

        string trimmed = market.Trim();
        int dash = trimmed.IndexOf('-');
        if (dash <= 0 || dash == trimmed.Length - 1)
        {
            return trimmed;
        }

        string language = trimmed.Substring(0, dash).ToLowerInvariant();
        string region = trimmed.Substring(dash + 1).ToUpperInvariant();
        return language + "-" + region;
    }

    /// <summary>
    /// Keeps a hand edited PinnedWallpaper value to a bare file name. Anything that
    /// carries a directory - "..\..\boot.ini", "C:\Windows\x.jpg" - is dropped
    /// rather than repaired: the program writes this value itself, so one that does
    /// not look like one is a mistake, not an instruction.
    /// </summary>
    private static string SanitizeFileName(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal))
        {
            return trimmed;
        }

        Logger.Warn("Ignoring PinnedWallpaper, it is not a plain file name: " + trimmed);
        return string.Empty;
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
            {
                return values;
            }

            bool inSection = false;
            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#')
                {
                    continue;
                }

                if (line[0] == '[')
                {
                    int end = line.IndexOf(']');
                    string section = end > 1 ? line.Substring(1, end - 1) : string.Empty;
                    inSection = string.Equals(section, SectionName, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                int sep = line.IndexOf('=');
                if (sep <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, sep).Trim();
                string value = line.Substring(sep + 1).Trim();
                values[key] = value;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to read configuration file: " + path, ex);
        }

        return values;
    }

    private static string GetString(Dictionary<string, string> values, string key, string fallback)
        => values.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int GetInt(Dictionary<string, string> values, string key, int fallback)
        => values.TryGetValue(key, out string? value)
           && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : fallback;

    private static bool GetBool(Dictionary<string, string> values, string key, bool fallback)
    {
        if (!values.TryGetValue(key, out string? value))
        {
            return fallback;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => fallback,
        };
    }

    private static ResolutionKind ParseResolution(string value)
    {
        string normalized = value.Trim().ToLowerInvariant();
        return normalized switch
        {
            "uhd" or "4k" => ResolutionKind.Uhd,
            "1920x1080" or "1080p" or "fullhd" => ResolutionKind.FullHd,
            _ => ResolutionKind.Uhd,
        };
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse(value.Trim(), ignoreCase: true, out TEnum parsed) ? parsed : fallback;

    /// <summary>Named wrapper so the call sites read the same as they always did.</summary>
    public static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);
}
