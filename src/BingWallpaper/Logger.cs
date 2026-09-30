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
    private static readonly int NewLineBytes = Utf8NoBom.GetByteCount(Environment.NewLine);

    private static string? _filePath;
    private static bool _fileDisabled;
    private static StreamWriter? _writer;
    private static long _fileBytes;

    // Info by default: Debug carries platform probing details that would otherwise
    // flood the 512 KiB rotation window and push out the startup banner.
    private static LogLevel _minimumLevel = LogLevel.Info;

    /// <summary>Set once at startup; a null path disables file logging.</summary>
    public static void Initialize(string? filePath)
    {
        lock (Sync)
        {
            CloseWriter();
            _filePath = filePath;
            _fileDisabled = string.IsNullOrEmpty(filePath);
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            _fileDisabled = true;
            CloseWriter();
        }
    }

    /// <summary>Lines below this level are dropped before any formatting happens.</summary>
    public static void SetMinimumLevel(LogLevel level)
    {
        lock (Sync)
        {
            _minimumLevel = level;
        }
    }

    /// <summary>Lets callers skip building expensive messages that would be dropped.</summary>
    public static bool IsEnabled(LogLevel level)
    {
        lock (Sync)
        {
            return level >= _minimumLevel;
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
        AppendException(sb, ex, 0);
        return sb.ToString().TrimEnd();
    }

    private static void AppendException(StringBuilder sb, Exception ex, int depth)
    {
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
                    if (depth + 1 >= 10)
                    {
                        break;
                    }

                    sb.AppendLine("--- aggregated ---");
                    AppendException(sb, item, depth + 1);
                }

                break;
            }

            current = current.InnerException;
            depth++;
        }
    }

    private static void Write(LogLevel level, string message)
    {
        lock (Sync)
        {
            if (level < _minimumLevel || _fileDisabled || _filePath is null)
            {
                return;
            }

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] [{2,3}] {3}",
                DateTime.Now,
                level.ToString().ToUpperInvariant(),
                Environment.CurrentManagedThreadId,
                message);

            try
            {
                EnsureWriter(_filePath);
                if (_fileBytes >= MaxFileBytes)
                {
                    CloseWriter();
                    Rotate(_filePath);
                    EnsureWriter(_filePath);
                }

                _writer!.WriteLine(line);
                _fileBytes += Utf8NoBom.GetByteCount(line) + NewLineBytes;
            }
            catch
            {
                // Logging must never take the application down. Disable the file
                // sink after the first failure so we do not retry on every line.
                _fileDisabled = true;
                CloseWriter();
            }
        }
    }

    private static void EnsureWriter(string path)
    {
        if (_writer is not null)
        {
            return;
        }

        // Readers may inspect the log, but another writer must not invalidate
        // the byte count or interfere with rotation.
        FileStream stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        try
        {
            _fileBytes = stream.Seek(0, SeekOrigin.End);
            _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = true };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void CloseWriter()
    {
        StreamWriter? writer = _writer;
        _writer = null;
        try
        {
            writer?.Dispose();
        }
        catch
        {
            // AutoFlush already wrote completed lines; a failed close must
            // not mask an application error or prevent shutdown.
        }
    }

    private static void Rotate(string path)
    {
        string backup = path + ".1";
        if (File.Exists(backup))
        {
            File.Delete(backup);
        }

        File.Move(path, backup);
    }
}
