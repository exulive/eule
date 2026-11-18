using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Eule.Core;
using Eule.Core.Localization;
using Eule.Core.Settings;
using Serilog;

namespace Eule.App;

public partial class App : Application
{
    Mutex? singleInstance;
    AppHost? host;
    Shell? shell;

    protected override void OnStartup(StartupEventArgs e)
    {
        var paths = AppPaths.Default;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(System.IO.Path.Combine(paths.LogDir, "eule-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log.Fatal(a.ExceptionObject as Exception, "Yakalanmamış hata");
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Error(a.Exception, "Gözlenmemiş görev hatası");
            a.SetObserved();
        };

        // Language first, so even the very first message is in the right one.
        var store = new SettingsStore(paths.SettingsFile);
        L.Lang = store.Exists ? store.Load().Language : L.FromSystem();

        int iconArg = Array.IndexOf(e.Args, "--export-icon");
        if (iconArg >= 0 && iconArg + 1 < e.Args.Length)
        {
            new Theme.ThemeService().Apply(AppTheme.Light);
            Interop.IconExporter.Export(e.Args[iconArg + 1]);
            Shutdown();
            return;
        }

        singleInstance = new Mutex(true, "Eule_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show(L.T("app.running"), "Eule", MessageBoxButton.OK, MessageBoxImage.Information);
            singleInstance.Dispose();
            singleInstance = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Log.Information("Eule başlıyor (sürüm {Version}, dil {Lang})", UpdateService.CurrentVersion, L.Lang);

        host = new AppHost(paths) { SeedDemo = e.Args.Contains("--seed-demo") };
        host.Start();
        L.Lang = host.Settings.Language;
        shell = new Shell(host, new UpdateService());
        shell.Start();

        // Developer shortcuts for trying the UI without waiting.
        if (e.Args.Contains("--welcome")) shell.OpenWelcome();
        if (e.Args.Contains("--settings")) shell.OpenSettings();
        if (e.Args.Contains("--stats")) shell.OpenStats();
        if (e.Args.Contains("--break")) host.Do(x => x.StartBreakNow());
        if (e.Args.Contains("--eye")) shell.PreviewEyeBreak();
    }

    /// <summary>Starts a fresh copy and closes this one (used after a language change).</summary>
    public static void Restart()
    {
        var app = (App)Current;
        Log.Information("Yeniden başlatılıyor");
        try
        {
            app.singleInstance?.ReleaseMutex();
        }
        catch (ApplicationException) { }
        app.singleInstance?.Dispose();
        app.singleInstance = null;
        if (Environment.ProcessPath is string exe) Process.Start(exe);
        app.Shutdown();
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Arayüz hatası");
        e.Handled = true;
        MessageBox.Show(L.F("app.error", AppPaths.Default.LogDir), "Eule", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        shell?.Dispose();
        host?.Dispose();
        Log.Information("Eule kapandı");
        Log.CloseAndFlush();
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}
