using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Eule.Core;
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

        singleInstance = new Mutex(true, "Eule_SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Eule zaten çalışıyor.", "Eule", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Log.Information("Eule başlıyor (sürüm {Version})", typeof(App).Assembly.GetName().Version);

        host = new AppHost(paths);
        host.Start();
        shell = new Shell(host);
        shell.Start();

        // Developer shortcuts for trying the UI without waiting.
        if (e.Args.Contains("--settings")) shell.OpenSettings();
        if (e.Args.Contains("--break")) host.Do(x => x.StartBreakNow());
        if (e.Args.Contains("--eye")) shell.PreviewEyeBreak();
    }

    void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Arayüz hatası");
        e.Handled = true;
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu, Eule çalışmaya devam ediyor.\n\nAyrıntılar günlük dosyasına yazıldı:\n" +
            AppPaths.Default.LogDir,
            "Eule", MessageBoxButton.OK, MessageBoxImage.Warning);
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
