using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Net.Http;
using Eule.Core;
using Eule.Core.Cloud;
using Eule.Core.Engine;
using Eule.Core.Settings;
using Eule.Core.Storage;
using Eule.App.Interop;
using Serilog;

namespace Eule.App;

/// <summary>
/// Owns the engine, settings and statistics, and feeds the engine once a second from a real clock.
/// Elapsed time is measured, not assumed, so a delayed timer or a sleeping PC is reported correctly.
/// </summary>
public sealed class AppHost : IDisposable
{
    readonly AppPaths paths;
    readonly Stopwatch clock = new();
    readonly BusyDetector busyDetector = new();
    DispatcherTimer? timer;
    double carry;
    int ticks;

    public AppHost(AppPaths paths) => this.paths = paths;

    /// <summary>Developer flag (--seed-demo): fill demo data. Only honoured with EULE_DATA_DIR set, never on real data.</summary>
    public bool SeedDemo { get; init; }

    public SettingsStore SettingsStore { get; private set; } = null!;
    public EuleSettings Settings { get; private set; } = null!;
    public EuleDatabase Database { get; private set; } = null!;
    public BreakEngine Engine { get; private set; } = null!;
    public StatsRecorder Stats { get; private set; } = null!;

    /// <summary>What the user was busy with on the last tick (presentation, call, full screen).</summary>
    public BusyReason Busy { get; private set; }

    /// <summary>Current streak; recomputed only when today's numbers or the goal change.</summary>
    public StreakInfo Streak
    {
        get
        {
            var t = Stats.Today;
            var key = (t.Date, t.AllBreaks, t.EmergencyExits, t.SitSeconds >= Core.Storage.Streak.RestDaySeconds, Settings.DailyBreakGoal);
            if (streakCache == null || streakKey != key)
            {
                streakKey = key;
                streakCache = Core.Storage.Streak.Compute(Database, t, Settings.DailyBreakGoal);
            }
            return streakCache;
        }
    }

    StreakInfo? streakCache;
    (DateOnly, int, int, bool, int) streakKey;

    /// <summary>Raised after every tick and every command, for the UI to refresh.</summary>
    public event Action? Updated;

    public void Start()
    {
        SettingsStore = new SettingsStore(paths.SettingsFile);
        string backupDir = System.IO.Path.Combine(paths.DataDir, "yedek-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Database = EuleDatabase.OpenOrRepair(paths.DatabaseFile, backupDir, out var repair);
        DatabaseRepair = repair;
        if (repair != EuleDatabase.Repair.None)
            Log.Warning("Veritabanı açılamadı ve onarıldı: {Repair}. Eski dosyalar: {Dir}", repair, backupDir);

        try
        {
            bool hadSettings = SettingsStore.Exists;
        var imported = LegacyImporter.Run(paths, SettingsStore, Database);
        if (!hadSettings && imported.SettingsImported) FirstRun = true;
            if (imported.SettingsImported || imported.DaysImported > 0)
                Log.Information("Oturma Sayacı verileri aktarıldı: ayarlar={Settings}, gün={Days}",
                    imported.SettingsImported, imported.DaysImported);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Eski veriler aktarılamadı");
        }

        if (SeedDemo && paths.IsOverridden && Database.GetMeta("demo") == null)
        {
            DemoData.Seed(Database, DateOnly.FromDateTime(DateTime.Today));
            Database.SetMeta("demo", "1");
            Log.Information("Demo verisi yüklendi: {Dir}", paths.DataDir);
        }

        Settings = SettingsStore.Load();
        if (SettingsStore.LastLoadError != null)
            Log.Warning("Ayar dosyası okunamadı, varsayılanlar kullanılıyor: {Error}", SettingsStore.LastLoadError);
        if (!SettingsStore.Exists)
        {
            FirstRun = true;
            Settings.Language = Core.Localization.L.FromSystem();
            SettingsStore.Save(Settings);
        }

        StartCloud();

        Engine = new BreakEngine(Settings);
        Stats = new StatsRecorder(Database, () => DateTime.Now);
        Stats.Attach(Engine);
        Engine.Raised += LogEvent;

        clock.Start();
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => OnTick();
        timer.Start();
        Log.Information("Motor başladı: {Work} dk oturma / {Break} dk mola, seviye {Level}",
            Settings.WorkMinutes, Settings.BreakMinutes, Settings.Level);
    }

    void OnTick()
    {
        carry += clock.Elapsed.TotalSeconds;
        clock.Restart();
        int seconds = (int)carry;
        if (seconds <= 0) return;
        carry -= seconds;

        // Only ask when it matters: detection walks the registry, and our own break screen is full screen.
        Busy = Engine.Phase is Phase.Working or Phase.Deferred or Phase.BreakDue ? busyDetector.Current : BusyReason.None;
        Engine.Tick(new EnvironmentSnapshot(DateTime.Now, IdleInput.Seconds(), Busy), seconds);
        if (++ticks % 30 == 0) Stats.Flush();
        Updated?.Invoke();
    }

    // ------------------------------------------------------------------ server (notes, dictation)

    public EuleApiClient Api { get; private set; } = null!;
    /// <summary>Dictation: speech to text on this computer.</summary>
    public Speech.SpeechService Speech { get; } = new(AppPaths.ModelDir);
    public DictationService Dictations { get; private set; } = null!;
    DispatcherTimer? flushTimer;

    void StartCloud()
    {
        var http = new HttpClient { BaseAddress = new Uri(Settings.ServerUrl), Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Eule/{UpdateService.CurrentVersion}");
        Api = new EuleApiClient(http, new DpapiAccountStore(paths.AccountFile));
        Dictations = new DictationService(Api, Database);
        Api.SignedOut += () => Log.Information("Sunucu oturumu sona erdi");
        Api.SignedIn += () => _ = FlushOutboxAsync();
        Log.Information("Sunucu: {Server}, oturum: {Account}", Settings.ServerUrl, Api.Account?.Email ?? "yok");

        // Notes that could not be sent go out on their own: every minute and whenever the network comes back.
        flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        flushTimer.Tick += (_, _) => _ = FlushOutboxAsync();
        flushTimer.Start();
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = FlushOutboxAsync());
        };
    }

    public async Task FlushOutboxAsync()
    {
        try
        {
            int sent = await Dictations.FlushAsync();
            if (sent > 0) Log.Information("Bekleyen {Count} not sunucuya gönderildi", sent);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Bekleyen notlar gönderilemedi");
        }
    }

    /// <summary>Runs a user command against the engine and refreshes the UI.</summary>
    public void Do(Action<BreakEngine> command)
    {
        command(Engine);
        Updated?.Invoke();
    }

    public void SaveSettings(EuleSettings settings)
    {
        Settings = settings.Normalize();
        SettingsStore.Save(Settings);
        Engine.ApplySettings(Settings);
        Log.Information("Ayarlar kaydedildi");
        SettingsChanged?.Invoke();
        Updated?.Invoke();
    }

    /// <summary>Saves UI-only state (widget position…) without touching the engine.</summary>
    public void SaveUiState(Action<EuleSettings> change)
    {
        change(Settings);
        try { SettingsStore.Save(Settings); }
        catch (IOException ex) { Log.Warning(ex, "Arayüz durumu kaydedilemedi"); }
    }

    public event Action? SettingsChanged;

    /// <summary>What had to be done to open the database this time (normally nothing).</summary>
    public EuleDatabase.Repair DatabaseRepair { get; private set; }

    /// <summary>True when Eule started without a settings file (first launch).</summary>
    public bool FirstRun { get; private set; }

    static void LogEvent(EngineEvent e)
    {
        if (e is Sitting) return;
        Log.Information("{Event}", e);
    }

    public void Dispose()
    {
        timer?.Stop();
        flushTimer?.Stop();
        try { Stats?.Flush(final: true); } catch (Exception ex) { Log.Error(ex, "İstatistik yazılamadı"); }
        Database?.Dispose();
    }
}
