using Microsoft.Win32;

namespace BingWallpaper;

/// <summary>
/// "Run at startup" via the HKCU Run key. A Startup folder shortcut would require
/// IShellLink COM interop, so the registry value is used instead.
///
/// This is the only registry location the program writes on its own behalf, and
/// only while the user explicitly enables the feature.
/// </summary>
internal static class AutoStartManager
{
    public const string ValueName = "BingWallpaper";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Returns the raw Run value, or null when the value is absent.</summary>
    public static string? ReadValue()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) as string;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not read the Run registry key.", ex);
            return null;
        }
    }

    /// <summary>
    /// Writes the Run value. The path is wrapped in double quotes - without them
    /// Windows truncates paths that contain spaces.
    /// </summary>
    public static bool Enable()
    {
        string command = "\"" + Paths.ExecutablePath + "\"";
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
                                   ?? throw new InvalidOperationException("Could not open the Run registry key.");
            key.SetValue(ValueName, command, RegistryValueKind.String);
            Logger.Info("Auto start enabled: " + command);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not enable auto start.", ex);
            return false;
        }
    }

    /// <summary>Removes the Run value if present.</summary>
    public static bool Disable()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return true;
            }

            if (key.GetValue(ValueName) is null)
            {
                return true;
            }

            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Logger.Info("Auto start disabled, Run value removed.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not disable auto start.", ex);
            return false;
        }
    }

    /// <summary>
    /// Reconciles the Run key with the configuration on every start.
    ///
    /// A portable program moves around (USB stick, renamed folder, different drive
    /// letter). Without this self healing pass the Run value would silently point at
    /// a path that no longer exists while the settings dialog still shows "enabled".
    /// </summary>
    public static void Synchronize(bool desiredEnabled)
    {
        string? current = ReadValue();

        if (!desiredEnabled)
        {
            if (current is not null)
            {
                Logger.Info("Auto start is off in the configuration but the Run value exists - removing it.");
                Disable();
            }

            return;
        }

        if (current is null)
        {
            Logger.Info("Auto start is on in the configuration but the Run value is missing - creating it.");
            Enable();
            return;
        }

        string normalizedCurrent = NormalizeCommand(current);
        string normalizedExpected = NormalizeCommand(Paths.ExecutablePath);
        if (string.Equals(normalizedCurrent, normalizedExpected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Logger.Info(
            "Auto start path drifted, updating the Run value. old=\"" + current +
            "\" new=\"" + Paths.ExecutablePath + "\"");
        Enable();
    }

    /// <summary>Strips quotes and normalizes a command line into a comparable full path.</summary>
    private static string NormalizeCommand(string command)
    {
        string value = command.Trim();
        if (value.Length >= 2 && value[0] == '"')
        {
            int closing = value.IndexOf('"', 1);
            value = closing > 0 ? value[1..closing] : value[1..];
        }
        else
        {
            int space = value.IndexOf(' ');
            if (space > 0 && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == false)
            {
                // Unquoted value with arguments: keep the first token only.
                value = value[..space];
            }
        }

        value = value.Trim();
        try
        {
            return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return value;
        }
    }
}
