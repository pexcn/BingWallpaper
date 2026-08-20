using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BingWallpaper.Theme;
using BingWallpaper.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace BingWallpaper;

/// <summary>
/// Startup, shutdown and the single instance guard.
///
/// The program takes no command line arguments: everything it can be told is in
/// BingWallpaper.ini next to the executable, and it is started from Explorer or
/// the Run key, never from a shell.
/// </summary>
public partial class App : Application
{
    private const string SingleInstanceObject = "BingWallpaper.SingleInstance";
    private const string ActivateObject = "BingWallpaper.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private AppController? _controller;
    private DispatcherQueue? _dispatcher;

    public App()
    {
        InitializeComponent();

        // There is no main window: the tray icon is the application. Without this,
        // WinUI would end the process the moment the settings window is closed.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;

        // The exception hooks come first: a crash before this point would be invisible.
        UnhandledException += OnXamlUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
        _dispatcher = dispatcher;

        // Portable by contract: no silent fallback to %LOCALAPPDATA%.
        if (!Paths.IsBaseDirectoryWritable(out string? writeError))
        {
            Logger.Initialize(null);
            NativeMethods.ShowError(
                "目录不可写",
                "必应壁纸是一个便携程序，它的配置、日志和壁纸都保存在程序所在的文件夹里。\r\n\r\n" +
                "当前目录不可写：\r\n" + Paths.BaseDirectory + "\r\n\r\n" +
                "原因：" + writeError + "\r\n\r\n" +
                "请把程序移动到有写入权限的目录（例如用户目录或 U 盘）后重新运行。");
            Environment.Exit(2);
            return;
        }

        Logger.Initialize(Paths.LogFile);
        Logger.Info("----------------------------------------------------------------");
        LogEnvironment();

        if (!TryBecomePrimaryInstance())
        {
            Logger.Info("Another instance is already running; asked it to show its settings window.");
            Environment.Exit(0);
            return;
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

            ThemeManager.Initialize(config.Theme);

            // Portable programs move around; keep the Run key in sync with reality.
            AutoStartManager.Synchronize(config.RunAtStartup);

            Paths.EnsureWallpaperDirectory();

            _controller = new AppController(config, dispatcher);
            _controller.ExitRequested += (_, _) => Shutdown();
            StartActivationListener();
            _controller.Start();
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during startup.", ex);
            ErrorWindow.Show("启动失败", Logger.Describe(ex));
        }
    }

    /// <summary>One line of environment information, written on every start.</summary>
    private static void LogEnvironment()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
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

    /// <summary>Tears everything down and ends the message loop.</summary>
    private void Shutdown()
    {
        Logger.Info("Shutting down.");
        _controller?.Dispose();
        _controller = null;
        ReleaseSingleInstance();
        Exit();
    }

    /// <summary>
    /// Named mutex based single instance guard. The Global namespace needs
    /// SeCreateGlobalPrivilege, which a standard user does not have, so the Local
    /// namespace is used as a fallback.
    /// </summary>
    private bool TryBecomePrimaryInstance()
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
            Logger.Warn("Could not signal the running instance: " + ex.Message);
        }
    }

    /// <summary>Waits for a second instance to ask for the settings window.</summary>
    private void StartActivationListener()
    {
        EventWaitHandle? handle = _activateEvent;
        DispatcherQueue? dispatcher = _dispatcher;
        if (handle is null || dispatcher is null)
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
                    dispatcher.TryEnqueue(() => _controller?.ShowSettings());
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

    private void ReleaseSingleInstance()
    {
        try
        {
            _activateEvent?.Dispose();
            _activateEvent = null;
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

    private void OnXamlUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI thread exception.", e.Exception);

        // Handled, then shown: an unhandled exception in a XAML handler would
        // otherwise take the tray icon down with it.
        e.Handled = true;
        ErrorWindow.Show("未处理的异常", Logger.Describe(e.Exception));
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Exception? ex = e.ExceptionObject as Exception;
        Logger.Error(
            "Unhandled AppDomain exception (terminating=" + e.IsTerminating + ").",
            ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }
}
