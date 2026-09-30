using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using BingWallpaper.Theme;
using BingWallpaper.UI;

namespace BingWallpaper;

internal static class Program
{
    private const string SingleInstanceObject = "BingWallpaper.SingleInstance";

    private static Mutex? _instanceMutex;

    /// <summary>
    /// The program takes no command line arguments: everything it can be told is in
    /// BingWallpaper.ini next to the executable, and it is started from Explorer or
    /// the Run key, never from a shell.
    /// </summary>
    [STAThread]
    private static int Main() => RunGui();

    /// <summary>One line of environment information, written on every start.</summary>
    private static void LogEnvironment(bool writable, string? writeError)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        Logger.Info(
            "startup: version=" + version +
            " os=" + Environment.OSVersion.Version +
            " build=" + Environment.OSVersion.Version.Build +
            " 64bit=" + Environment.Is64BitProcess +
            " dpi=" + NativeMethods.GetSystemDpiSafe().ToString(CultureInfo.InvariantCulture) +
            " systemtheme=" + (ThemeManager.IsSystemDark() ? "Dark" : "Light") +
            " dir=" + Paths.BaseDirectory +
            " writable=" + writable + (writable ? string.Empty : " writeerror=" + writeError));
    }

    private static int RunGui()
    {
        // The exception hooks come first: a crash before this point would be invisible.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Portable by contract: no silent fallback to %LOCALAPPDATA%.
        bool writable = Paths.IsBaseDirectoryWritable(out string? writeError);
        if (!writable)
        {
            Logger.Initialize(null);
            MessageBox.Show(
                "必应壁纸是一个便携程序，它的配置、日志和壁纸都保存在程序所在的文件夹里。\r\n\r\n" +
                "当前目录不可写：\r\n" + Paths.BaseDirectory + "\r\n\r\n" +
                "原因：" + writeError + "\r\n\r\n" +
                "请把程序移动到有写入权限的目录（例如用户目录或 U 盘）后重新运行。",
                "目录不可写",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 2;
        }

        // Only the primary instance owns the log writer. A duplicate launch
        // must not open the same file while the primary is appending or rotating.
        if (!TryBecomePrimaryInstance(out List<string> instanceWarnings))
        {
            return 0;
        }

        Logger.Initialize(Paths.LogFile);
        foreach (string warning in instanceWarnings)
        {
            Logger.Warn(warning);
        }

        // The only decorative line in the log, and only because a process boundary is
        // what you look for first when a log spans several runs.
        Logger.Info("----------------------------------------------------------------");
        LogEnvironment(writable, writeError);

        try
        {
            AppConfig config = AppConfig.Load(Paths.ConfigFile);
            Logger.SetMinimumLevel(config.LogLevel);
            if (!File.Exists(Paths.ConfigFile))
            {
                Logger.Info("config: no file found, writing defaults path=" + Paths.ConfigFile);
                config.Save(Paths.ConfigFile);
            }

            Logger.Info(
                "config: market=" + config.Market +
                " resolution=" + AppConfig.ResolutionToString(config.Resolution) +
                " fit=" + config.Fit +
                " fade=" + config.FadeTransition +
                " theme=" + config.Theme +
                " interval=" + config.RefreshIntervalHours + "h" +
                " keepdays=" + config.KeepDays +
                " runatstartup=" + config.RunAtStartup +
                " loglevel=" + config.LogLevel +
                " pinned=" + (config.IsPinned ? config.PinnedWallpaper : "none"));

            ThemeManager.Initialize(config.Theme);

            // Portable programs move around; keep the Run key in sync with reality.
            AutoStartManager.Synchronize(config.RunAtStartup);

            Paths.EnsureWallpaperDirectory();

            Application.Run(new TrayContext(config));
            Logger.Info("shutdown: message loop finished");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("startup: fatal error", ex);
            ErrorDialog.Show("启动失败", Logger.Describe(ex));
            return 1;
        }
        finally
        {
            Logger.Shutdown();
            ReleaseSingleInstance();
        }
    }

    /// <summary>
    /// Named mutex based single instance guard. The Global namespace needs
    /// SeCreateGlobalPrivilege, which a standard user does not have, so the Local
    /// namespace is used as a fallback.
    /// </summary>
    private static bool TryBecomePrimaryInstance(out List<string> warnings)
    {
        warnings = new List<string>();
        foreach (string prefix in new[] { @"Global\", @"Local\" })
        {
            try
            {
                bool createdNew;
                Mutex mutex = new Mutex(true, prefix + SingleInstanceObject, out createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                _instanceMutex = mutex;
                return true;
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "singleinstance: create failed namespace=" + prefix.TrimEnd('\\') +
                    " error=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Fail open: running without the guard is better than not running at all.
        warnings.Add("singleinstance: continuing without a guard");
        return true;
    }

    private static void ReleaseSingleInstance()
    {
        try
        {
            if (_instanceMutex is not null)
            {
                _instanceMutex.ReleaseMutex();
                _instanceMutex.Dispose();
                _instanceMutex = null;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug("singleinstance: release failed error=" + ex.Message);
        }
    }

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Logger.Error("crash: unhandled ui thread exception", e.Exception);
        ErrorDialog.Show("未处理的异常", Logger.Describe(e.Exception));
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? ex = e.ExceptionObject as Exception;
        Logger.Error("crash: unhandled appdomain exception terminating=" + e.IsTerminating, ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        ErrorDialog.Show("未处理的异常", Logger.Describe(ex));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("crash: unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private static void OnProcessExit(object sender, EventArgs e)
    {
        Logger.Shutdown();
    }

}
