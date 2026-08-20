using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace BingWallpaper;

internal enum LogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// Minimal file logger with size based rotation. The user of this program has no
/// debugger, so the log is the primary diagnostic channel: it must never throw
/// and must never lose an exception.
/// </summary>
internal static class Logger
{
    private const long MaxFileBytes = 512 * 1024;

    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static string? _filePath;
    private static bool _fileDisabled;

    /// <summary>When true every line is also written to stdout (used by --selftest).</summary>
    public static bool EchoToConsole { get; set; }

    /// <summary>Set once at startup; a null path disables file logging.</summary>
    public static void Initialize(string? filePath)
    {
        lock (Sync)
        {
            _filePath = filePath;
            _fileDisabled = string.IsNullOrEmpty(filePath);
        }
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Error(string context, Exception ex) => Write(LogLevel.Error, context + Environment.NewLine + Describe(ex));

    /// <summary>Formats the complete exception chain including stack traces.</summary>
    public static string Describe(Exception? ex)
    {
        if (ex is null)
        {
            return "(no exception)";
        }

        StringBuilder sb = new StringBuilder();
        int depth = 0;
        Exception? current = ex;
        while (current is not null && depth < 10)
        {
            sb.Append(depth == 0 ? "Exception: " : "Inner exception: ");
            sb.Append(current.GetType().FullName);
            sb.Append(": ");
            sb.AppendLine(current.Message);
            if (!string.IsNullOrEmpty(current.StackTrace))
            {
                sb.AppendLine(current.StackTrace);
            }

            if (current is AggregateException aggregate)
            {
                foreach (Exception item in aggregate.InnerExceptions)
                {
                    sb.AppendLine("--- aggregated ---");
                    sb.AppendLine(Describe(item));
                }

                break;
            }

            current = current.InnerException;
            depth++;
        }

        return sb.ToString().TrimEnd();
    }

    private static void Write(LogLevel level, string message)
    {
        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [{2,3}] {3}",
            DateTime.Now,
            level.ToString().ToUpperInvariant(),
            Environment.CurrentManagedThreadId,
            message);

        lock (Sync)
        {
            if (EchoToConsole)
            {
                try
                {
                    Console.Out.WriteLine(line);
                    Console.Out.Flush();
                }
                catch
                {
                    // No console attached - ignore.
                }
            }

            if (_fileDisabled || _filePath is null)
            {
                return;
            }

            try
            {
                RotateIfNeeded(_filePath);
                File.AppendAllText(_filePath, line + Environment.NewLine, Utf8NoBom);
            }
            catch
            {
                // Logging must never take the application down. Disable the file
                // sink after the first failure so we do not retry on every line.
                _fileDisabled = true;
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        FileInfo info = new FileInfo(path);
        if (!info.Exists || info.Length < MaxFileBytes)
        {
            return;
        }

        string backup = path + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(path, backup);
    }
}
