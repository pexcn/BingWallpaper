using System;
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
    private const string ActivateObject = "BingWallpaper.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;
    private static TrayContext? _trayContext;

    /// <summary>
    /// The program takes no command line arguments: everything it can be told is in
    /// BingWallpaper.ini next to the executable, and it is started from Explorer or
    /// the Run key, never from a shell.
    /// </summary>
    [STAThread]
    private static int Main() => RunGui();

    /// <summary>One line of environment information, written on every start.</summary>
    private static void LogEnvironment()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        bool writable = Paths.IsBaseDirectoryWritable(out string? writeError);
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
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Portable by contract: no silent fallback to %LOCALAPPDATA%.
        if (!Paths.IsBaseDirectoryWritable(out string? writeError))
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

        Logger.Initialize(Paths.LogFile);

        // The only decorative line in the log, and only because a process boundary is
        // what you look for first when a log spans several runs.
        Logger.Info("----------------------------------------------------------------");
        LogEnvironment();

        if (!TryBecomePrimaryInstance())
        {
            Logger.Info("startup: another instance is primary, asked it to show settings");
            return 0;
        }

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

            _trayContext = new TrayContext(config);
            StartActivationListener();
            Application.Run(_trayContext);
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
            ReleaseSingleInstance();
        }
    }

    /// <summary>
    /// Named mutex based single instance guard. The Global namespace needs
    /// SeCreateGlobalPrivilege, which a standard user does not have, so the Local
    /// namespace is used as a fallback.
    /// </summary>
    private static bool TryBecomePrimaryInstance()
    {
        foreach (string prefix in new[] { @"Global\", @"Local\" })
        {
            try
            {
                bool createdNew;
                Mutex mutex = new Mutex(true, prefix + SingleInstanceObject, out createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    SignalRunningInstance(prefix + ActivateObject);
                    return false;
                }

                _instanceMutex = mutex;
                _activateEvent = new EventWaitHandle(
                    false,
                    EventResetMode.AutoReset,
                    prefix + ActivateObject);
                Logger.Debug("singleinstance: namespace=" + prefix.TrimEnd('\\'));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "singleinstance: create failed namespace=" + prefix.TrimEnd('\\') +
                    " error=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Fail open: running without the guard is better than not running at all.
        Logger.Warn("singleinstance: continuing without a guard");
        return true;
    }

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            EventWaitHandle? handle;
            if (EventWaitHandle.TryOpenExisting(eventName, out handle) && handle is not null)
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("singleinstance: signalling the running instance failed error=" + ex.Message);
        }
    }

    /// <summary>Waits for a second instance to ask for the settings window.</summary>
    private static void StartActivationListener()
    {
        EventWaitHandle? handle = _activateEvent;
        TrayContext? context = _trayContext;
        if (handle is null || context is null)
        {
            return;
        }

        Thread thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    handle.WaitOne();
                    context.RequestActivation();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn("singleinstance: activation listener stopped error=" + ex.Message);
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "BingWallpaper.ActivationListener",
        };
        thread.Start();
    }

    private static void ReleaseSingleInstance()
    {
        try
        {
            _activateEvent?.Dispose();
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

}
