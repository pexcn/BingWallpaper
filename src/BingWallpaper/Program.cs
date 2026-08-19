using System.Globalization;
using System.Reflection;
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

    [STAThread]
    private static int Main(string[] args)
    {
        if (HasSwitch(args, "--selftest"))
        {
            AttachConsoleIfPossible();
            Logger.Initialize(Paths.IsBaseDirectoryWritable(out _) ? Paths.LogFile : null);
            return SelfTest.RunAsync(args).GetAwaiter().GetResult();
        }

        if (HasSwitch(args, "--help") || HasSwitch(args, "-h") || HasSwitch(args, "/?"))
        {
            AttachConsoleIfPossible();
            PrintUsage();
            return 0;
        }

        return RunGui();
    }

    /// <summary>One line of environment information, written on every start.</summary>
    public static void LogEnvironment()
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

    private static int RunGui()
    {
        // The exception hooks come first: a crash before this point would be invisible.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            // The manifest already declares PerMonitorV2; this is a no-op safety net.
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (Exception ex)
        {
            Logger.Warn("SetHighDpiMode failed: " + ex.Message);
        }

        // Portable by contract: no silent fallback to %LOCALAPPDATA%.
        if (!Paths.IsBaseDirectoryWritable(out string? writeError))
        {
            Logger.Initialize(null);
            MessageBox.Show(
                "BingWallpaper 是一个便携程序，它的配置、日志和壁纸都保存在程序所在的文件夹里。\r\n\r\n" +
                "当前目录不可写：\r\n" + Paths.BaseDirectory + "\r\n\r\n" +
                "原因：" + writeError + "\r\n\r\n" +
                "请把程序移动到有写入权限的目录（例如用户目录或 U 盘）后重新运行。",
                "BingWallpaper - 目录不可写",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
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
                " runAtStartup=" + config.RunAtStartup);

            ThemeManager.Initialize(config.Theme);

            // Portable programs move around; keep the Run key in sync with reality.
            AutoStartManager.Synchronize(config.RunAtStartup);

            Paths.EnsureWallpaperDirectory();

            _trayContext = new TrayContext(config);
            StartActivationListener();
            Application.Run(_trayContext);
            Logger.Info("Message loop finished, exiting.");
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("Fatal error during startup.", ex);
            ErrorDialog.Show("BingWallpaper - 启动失败", Logger.Describe(ex));
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
                Mutex mutex = new(initiallyOwned: true, prefix + SingleInstanceObject, out bool createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    SignalRunningInstance(prefix + ActivateObject);
                    return false;
                }

                _instanceMutex = mutex;
                _activateEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: prefix + ActivateObject);
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
        TrayContext? context = _trayContext;
        if (handle is null || context is null)
        {
            return;
        }

        Thread thread = new(() =>
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

    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Logger.Error("Unhandled UI thread exception.", e.Exception);
        ErrorDialog.Show("BingWallpaper - 未处理的异常", Logger.Describe(e.Exception));
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception? ex = e.ExceptionObject as Exception;
        Logger.Error("Unhandled AppDomain exception (terminating=" + e.IsTerminating + ").", ex ?? new Exception(e.ExceptionObject?.ToString() ?? "unknown"));
        ErrorDialog.Show("BingWallpaper - 未处理的异常", Logger.Describe(ex));
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private static bool HasSwitch(string[] args, string name)
        => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));

    private static void PrintUsage()
    {
        Console.Out.WriteLine("BingWallpaper - unofficial Bing daily wallpaper client (portable).");
        Console.Out.WriteLine();
        Console.Out.WriteLine("Usage: BingWallpaper.exe [options]");
        Console.Out.WriteLine("  (no options)            start in the notification area");
        Console.Out.WriteLine("  --selftest              head-less API + download check, exit code 0/1");
        Console.Out.WriteLine("      --market=zh-CN      override the market for the self test");
        Console.Out.WriteLine("      --resolution=1080p  override the resolution for the self test (uhd|1080p)");
        Console.Out.WriteLine("  --help                  show this text");
        Console.Out.Flush();
    }

    /// <summary>
    /// This is a GUI subsystem executable, so stdout is not connected by default.
    /// Attaching to the parent console makes --selftest usable from cmd/PowerShell/CI.
    /// </summary>
    private static void AttachConsoleIfPossible()
    {
        try
        {
            if (!NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS))
            {
                return;
            }

            StreamWriter writer = new(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch
        {
            // No console available (for example when launched from Explorer).
        }
    }
}
