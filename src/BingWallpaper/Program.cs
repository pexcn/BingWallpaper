using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using BingWallpaper.Theme;
using BingWallpaper.UI;

namespace BingWallpaper;

internal static class Program
{
    private const string SingleInstanceObject = "BingWallpaper.SingleInstance";
    private const string ActivateObject = "BingWallpaper.Activate";

    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateEvent;

    /// <summary>
    /// The program takes no command line arguments: everything it can be told is in
    /// BingWallpaper.ini next to the executable, and it is started from Explorer or
    /// the Run key, never from a shell.
    /// </summary>
    [STAThread]
    private static int Main() => RunGui();

    /// <summary>
    /// The Avalonia configuration. The backends are named instead of using
    /// UsePlatformDetect(): detection loads them by reflection, which is exactly
    /// what a Native AOT build has no way of resolving, and this program only ever
    /// runs on Windows anyway.
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia();

    /// <summary>One line of environment information, written on every start.</summary>
    private static void LogEnvironment()
    {
        string version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
        bool writable = Paths.IsBaseDirectoryWritable(out string? writeError);
        Logger.Info(
            "BingWallpaper " + version +
            " | OS " + Environment.OSVersion.Version +
            " (build " + Environment.OSVersion.Version.Build + ")" +
            " | 64bit=" + Environment.Is64BitProcess +
            " | DPI " + NativeMethods.GetSystemDpiSafe().ToString(CultureInfo.InvariantCulture) +
            " | system theme " + (ThemeManager.IsSystemDark() ? "Dark" : "Light") +
            " | dir " + Paths.BaseDirectory +
            " | writable " + writable + (writable ? string.Empty : " (" + writeError + ")"));
    }

    private static int RunGui()
    {
        // The exception hooks come first: a crash before this point would be invisible.
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Portable by contract: no silent fallback to %LOCALAPPDATA%.
        if (!Paths.IsBaseDirectoryWritable(out string? writeError))
        {
            Logger.Initialize(null);
            NativeMethods.ShowMessageBox(
                "目录不可写",
                "必应壁纸是一个便携程序，它的配置、日志和壁纸都保存在程序所在的文件夹里。\r\n\r\n" +
                "当前目录不可写：\r\n" + Paths.BaseDirectory + "\r\n\r\n" +
                "原因：" + writeError + "\r\n\r\n" +
                "请把程序移动到有写入权限的目录（例如用户目录或 U 盘）后重新运行。");
            return 2;
        }

        Logger.Initialize(Paths.LogFile);
        Logger.Info("----------------------------------------------------------------");
        LogEnvironment();

        if (!TryBecomePrimaryInstance())
        {
            Logger.Info("Another instance is already running; asked it to show its settings window.");
            return 0;
        }

        try
        {
            AppConfig config = AppConfig.Load(Paths.ConfigFile);
            if (!File.Exists(Paths.ConfigFile))
            {
                Logger.Info("No configuration file found, writing defaults to " + Paths.ConfigFile);
                config.Save(Paths.ConfigFile);
            }

            Logger.Info(
                "Configuration: market=" + config.Market +
                " resolution=" + AppConfig.ResolutionToString(config.Resolution) +
                " fit=" + config.Fit +
                " theme=" + config.Theme +
                " interval=" + config.RefreshIntervalHours + "h" +
                " keepDays=" + config.KeepDays +
                " runAtStartup=" + config.RunAtStartup +
                " pinned=" + (config.IsPinned ? config.PinnedWallpaper : "no"));

            // Portable programs move around; keep the Run key in sync with reality.
            AutoStartManager.Synchronize(config.RunAtStartup);

            Paths.EnsureWallpaperDirectory();

            App.Configuration = config;
            StartActivationListener();

            // OnExplicitShutdown: this program has no main window, and every window
            // it does open is closed again while the tray icon stays. The default
            // would end the process the first time the settings window is closed.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
                Array.Empty<string>(),
                ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during startup.", ex);
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
                Mutex mutex = new Mutex(true, prefix + SingleInstanceObject, out bool createdNew);
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
                Logger.Debug("Single instance guard uses the " + prefix.TrimEnd('\\') + " namespace.");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    "Could not create the single instance objects in the " + prefix.TrimEnd('\\') +
                    " namespace: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Fail open: running without the guard is better than not running at all.
        Logger.Warn("Continuing without a single instance guard.");
        return true;
    }

    private static void SignalRunningInstance(string eventName)
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(eventName, out EventWaitHandle? handle) && handle is not null)
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not signal the running instance: " + ex.Message);
        }
    }

    /// <summary>Waits for a second instance to ask for the settings window.</summary>
    private static void StartActivationListener()
    {
        EventWaitHandle? handle = _activateEvent;
        if (handle is null)
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
                    App.Controller?.RequestActivation();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Warn("Activation listener error: " + ex.Message);
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
            Logger.Debug("Releasing the single instance objects failed: " + ex.Message);
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? ex = e.ExceptionObject as Exception;
        Logger.Error(
            "Unhandled exception (terminating=" + e.IsTerminating + ").",
            ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        ErrorDialog.Show("未处理的异常", Logger.Describe(ex));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
