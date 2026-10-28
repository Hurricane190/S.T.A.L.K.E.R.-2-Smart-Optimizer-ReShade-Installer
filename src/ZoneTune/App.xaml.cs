using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ZoneTune.Core;

namespace ZoneTune;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Threading.Thread.CurrentThread.CurrentCulture = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;

        Log.Initialize();
        _mutex = new Mutex(true, "Global\\ZoneTune-STALKER2-SmartOptimizer", out var isNew);
        if (!isNew)
        {
            MessageBox.Show("ZoneTune is already running.", "ZoneTune", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            WriteCrash(ex, "AppDomain");
            MessageBox.Show("ZoneTune hit an unexpected error. A crash log was written to:\n\n" + AppPaths.LogsDir +
                            "\n\n" + ex?.Message, "ZoneTune error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception, "Dispatcher");
        Log.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show("A UI error occurred:\n\n" + e.Exception.Message +
                        "\n\nThe error was logged. You can continue, but restart ZoneTune if the interface behaves unexpectedly.\n\nLog: " +
                        AppPaths.LogsDir, "ZoneTune error", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void WriteCrash(Exception? ex, string source)
    {
        try
        {
            var path = Path.Combine(AppPaths.LogsDir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new StringBuilder();
            sb.AppendLine($"ZoneTune {AppPaths.Version} crash report");
            sb.AppendLine($"Time: {DateTime.Now:O}");
            sb.AppendLine($"Source: {source}");
            sb.AppendLine($"OS: {Environment.OSVersion}");
            sb.AppendLine($"Runtime: {Environment.Version}");
            sb.AppendLine($"Process: {Environment.ProcessPath}");
            sb.AppendLine();
            sb.AppendLine(ex?.ToString() ?? "Unknown exception");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log.Error($"Crash report written: {path}", ex);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        Log.Info("ZoneTune exiting.");
        base.OnExit(e);
    }
}
