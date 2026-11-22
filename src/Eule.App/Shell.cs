using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Eule.App.Theme;
using Eule.App.Views;
using Eule.Core.Engine;
using Eule.Core.Localization;
using Microsoft.Win32;
using Serilog;

namespace Eule.App;

/// <summary>Owns every surface (tray, widget, break screen, settings) and keeps them in step with the engine.</summary>
public sealed class Shell : IDisposable
{
    readonly AppHost host;
    readonly ThemeService theme = new();
    TrayIcon tray = null!;
    WidgetWindow widget = null!;
    BreakWindow? breakWindow;
    readonly List<CoverWindow> covers = [];
    SettingsWindow? settingsWindow;
    EyeBreakWindow? eyeWindow;
    /// <summary>The widget was hidden by the user but is shown while Eule needs an answer.</summary>
    bool widgetShownForPrompt;
    /// <summary>The day the "goal met" message was shown, so it appears once per day.</summary>
    DateOnly? goalCelebrated;

    readonly ContextMenu menu = new();
    readonly Window menuHost = new()
    {
        WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
        ShowInTaskbar = false, Width = 1, Height = 1, Left = -10000, Top = -10000, Topmost = true,
    };
    MenuItem miStatus = null!, miToday = null!, miBreak = null!, miPause = null!, miReset = null!, miWidget = null!, miAccount = null!;

    readonly UpdateService updates;
    readonly System.Windows.Threading.DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    WelcomeWindow? welcomeWindow;

    public Shell(AppHost host, UpdateService updates)
    {
        this.host = host;
        this.updates = updates;
    }

    public void Start()
    {
        theme.Apply(host.Settings.Theme);
        BuildMenu();

        tray = new TrayIcon();
        tray.LeftClick += () => SetWidgetVisible(true);
        tray.DoubleClick += OpenSettings;
        tray.RightClick += () => ShowMenu(null);

        widget = new WidgetWindow(host, this);
        widget.Collapsed += OnWidgetCollapsed;
        if (host.Settings.ShowWidget) widget.Show();

        host.Updated += Refresh;
        host.SettingsChanged += OnSettingsChanged;
        host.Engine.Raised += OnEngineEvent;
        theme.Changed += Refresh;
        if (host.Streak.TodayMet) goalCelebrated = host.Stats.Today.Date; // don't congratulate again after a restart

        StartupRegistration.Apply(host.Settings.StartWithWindows);
        Refresh();

        // Quiet background update: first check a minute after start, then once a day.
        if (updates.CanUpdate)
        {
            updateTimer.Tick += async (_, _) =>
            {
                updateTimer.Interval = TimeSpan.FromHours(24);
                var (info, _) = await updates.CheckAndDownloadAsync();
                if (info == null) return;
                updates.ApplyOnExit(info);
                updateTimer.Stop();
                tray.Notify(L.T("note.updateTitle"), L.T("note.updateText"));
            };
            updateTimer.Start();
        }

        host.Api.SignedOut += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            tray.Notify(L.T("note.signedOutTitle"), L.T("note.signedOutText"));
            Refresh();
            settingsWindow?.RefreshAccount();
        });
        host.Dictations.DeliveredLater += r => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            tray.Notify(L.T("note.deliveredTitle"), r.Reply);
            NotesChanged();
        });

        if (host.DatabaseRepair != Core.Storage.EuleDatabase.Repair.None)
            tray.Notify(L.T("db.repairedTitle"), L.T(host.DatabaseRepair == Core.Storage.EuleDatabase.Repair.JournalSetAside
                ? "db.repairedJournal" : "db.repairedFresh"));

        // Sign in first (first launch and whenever there is no account), then the welcome tour on first launch.
        if (!host.Api.IsSignedIn) OpenLogin(after: host.FirstRun ? OpenWelcome : null);
        else if (host.FirstRun) OpenWelcome();
        else if (!host.Settings.ShowWidget)
            tray.Notify(L.T("note.hereTitle"), L.T("note.hereText"));
        _ = host.FlushOutboxAsync();
    }

    // ------------------------------------------------------------------ account and notes

    LoginWindow? loginWindow;
    /// <summary>The widget is hidden by the user but shown while a note is being taken.</summary>
    bool widgetShownForNote;

    public void OpenLogin(Action? after = null)
    {
        if (loginWindow != null) { loginWindow.Activate(); return; }
        loginWindow = new LoginWindow(host);
        loginWindow.Closed += (_, _) =>
        {
            loginWindow = null;
            settingsWindow?.RefreshAccount();
            Refresh();
            after?.Invoke();
        };
        loginWindow.Show();
        loginWindow.Activate();
    }

    public async Task SignOutAsync()
    {
        await host.Api.LogoutAsync();
        Log.Information("Çıkış yapıldı");
        settingsWindow?.RefreshAccount();
        Refresh();
    }

    /// <summary>"Write a note" from the tray: the widget opens straight into typing (shown just for this if hidden).</summary>
    public void OpenNote()
    {
        if (!host.Api.IsSignedIn) { OpenLogin(after: () => { if (host.Api.IsSignedIn) OpenNote(); }); return; }
        if (!widget.IsVisible)
        {
            widgetShownForNote = true;
            ShowWidgetWindow(true);
        }
        widget.OpenKeyboard();
    }

    /// <summary>Developer preview (--dictate): opens the microphone mode.</summary>
    public void OpenDictation()
    {
        if (!widget.IsVisible) { widgetShownForNote = true; ShowWidgetWindow(true); }
        widget.StartDictation();
    }

    void OnWidgetCollapsed()
    {
        if (!widgetShownForNote) return;
        widgetShownForNote = false;
        if (!host.Settings.ShowWidget && !widgetShownForPrompt) ShowWidgetWindow(false);
    }

    // ------------------------------------------------------------------ state → surfaces

    void Refresh()
    {
        var e = host.Engine;
        string status = Present.Status(e);
        tray.Update(e, status);
        UpdatePromptWidget(e);
        if (widget.IsVisible) widget.Refresh();

        bool breakScreen = e.Phase is Phase.Break || (e.Phase is Phase.BreakFinished && breakWindow != null);
        if (breakScreen && breakWindow == null) OpenBreakScreens(e);
        else if (!breakScreen && breakWindow != null) CloseBreakScreens();
        breakWindow?.Refresh();
        foreach (var c in covers) c.Refresh();

        miStatus.Header = status;
        var t = host.Stats.Today;
        var streak = host.Streak;
        miToday.Header = L.F("menu.today", Present.Duration(t.SitSeconds), t.AllBreaks, streak.Goal) +
                         (streak.Days > 0 ? L.F("menu.streak", streak.Days) : "");
        miBreak.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue;
        miPause.Header = L.T(e.Phase == Phase.Paused ? "menu.resume" : "menu.pause");
        miPause.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue or Phase.Away;
        miReset.IsEnabled = e.Phase is not Phase.Break;
        miWidget.IsChecked = widget.IsVisible;
        var account = host.Api.Account;
        miAccount.Header = account == null ? L.T("menu.signIn") : $"{L.T("set.signOut")} ({account.Email})";
    }

    void OpenBreakScreens(BreakEngine e)
    {
        eyeWindow?.Close();
        breakWindow = new BreakWindow(host, Interop.ScreenPlacement.Primary);
        breakWindow.Show();
        breakWindow.Activate();

        // A strict break always covers every monitor; otherwise it is a setting.
        if (e.IsStrictBreak || host.Settings.CoverAllMonitors)
        {
            double fade = e.IsStrictBreak ? BreakWindow.StrictDimSeconds : 0.7;
            foreach (var screen in Interop.ScreenPlacement.Others)
            {
                var cover = new CoverWindow(host, screen, fade);
                covers.Add(cover);
                cover.Show();
            }
        }
        breakWindow.Activate();
    }

    void CloseBreakScreens()
    {
        breakWindow?.CloseByCode();
        breakWindow = null;
        foreach (var c in covers) c.CloseByCode();
        covers.Clear();
    }

    /// <summary>When the widget is hidden, it still comes out to ask for the break, then goes away again.</summary>
    void UpdatePromptWidget(BreakEngine e)
    {
        bool needsAnswer = e.Phase is Phase.BreakDue or Phase.Deferred;
        if (needsAnswer && !widget.IsVisible)
        {
            widgetShownForPrompt = true;
            ShowWidgetWindow(true);
        }
        else if (!needsAnswer && widgetShownForPrompt)
        {
            widgetShownForPrompt = false;
            if (!host.Settings.ShowWidget) ShowWidgetWindow(false);
        }
    }

    void ShowEyeBreak(int seconds)
    {
        // Not during a call, a presentation or a full-screen app, and never over a real break.
        if (host.Busy != BusyReason.None || breakWindow != null || eyeWindow != null) return;
        eyeWindow = new EyeBreakWindow(seconds);
        eyeWindow.Closed += (_, _) => eyeWindow = null;
        eyeWindow.Show();
    }

    /// <summary>Developer preview (--eye).</summary>
    public void PreviewEyeBreak() => ShowEyeBreak(host.Settings.EyeBreak.DurationSeconds);

    void CheckGoal()
    {
        var streak = host.Streak;
        if (!streak.TodayMet || goalCelebrated == host.Stats.Today.Date) return;
        goalCelebrated = host.Stats.Today.Date;
        tray.Notify(L.T("note.goalTitle"),
            streak.Days > 1 ? L.F("note.goalStreak", streak.Days) : L.T("note.goalText"));
    }

    void OnEngineEvent(EngineEvent ev)
    {
        var s = host.Settings;
        switch (ev)
        {
            case BreakStarted:
                Play(SystemSounds.Exclamation);
                break;
            case BreakDue due:
                if (!due.Repeat) Play(SystemSounds.Asterisk);
                break;
            case WarningRaised w when !widget.IsVisible:
                tray.Notify(L.T("note.warnTitle"), L.F("note.warnText", Math.Max(1, w.SecondsLeft / 60)));
                break;
            case BreakEnded { Reason: BreakEndReason.Completed }:
                Play(SystemSounds.Asterisk);
                Application.Current.Dispatcher.BeginInvoke(CheckGoal);
                break;
            case AwayCredited:
                Application.Current.Dispatcher.BeginInvoke(CheckGoal);
                break;
            case EyeBreakDue eye:
                ShowEyeBreak(eye.Seconds);
                break;
        }

        void Play(SystemSound sound)
        {
            if (s.Sound) sound.Play();
        }
    }

    void OnSettingsChanged()
    {
        var s = host.Settings;
        theme.Apply(s.Theme);
        StartupRegistration.Apply(s.StartWithWindows);
        if (s.ShowWidget != widget.IsVisible) ShowWidgetWindow(s.ShowWidget);
        Refresh();
    }

    // ------------------------------------------------------------------ commands

    public void SetWidgetVisible(bool visible)
    {
        host.SaveUiState(s => s.ShowWidget = visible);
        ShowWidgetWindow(visible);
        settingsWindow?.SyncFromSettings();
        Refresh();
    }

    void ShowWidgetWindow(bool visible)
    {
        if (visible)
        {
            widget.Show();
            widget.Refresh();
        }
        else
        {
            widget.Hide();
        }
    }

    public void OpenSettings()
    {
        if (settingsWindow != null)
        {
            if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
            settingsWindow.Activate();
            return;
        }
        settingsWindow = new SettingsWindow(host, updates);
        settingsWindow.OpenStatsRequested += OpenStats;
        settingsWindow.SignInRequested += () => OpenLogin();
        settingsWindow.SignOutRequested += () => _ = SignOutAsync();
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
    }

    public void OpenWelcome()
    {
        if (welcomeWindow != null) { welcomeWindow.Activate(); return; }
        welcomeWindow = new WelcomeWindow(host);
        welcomeWindow.Closed += (_, _) =>
        {
            welcomeWindow = null;
            ShowWidgetWindow(host.Settings.ShowWidget);
            Refresh();
        };
        welcomeWindow.Show();
        welcomeWindow.Activate();
    }

    StatsWindow? statsWindow;

    public void OpenStats()
    {
        if (statsWindow != null)
        {
            if (statsWindow.WindowState == WindowState.Minimized) statsWindow.WindowState = WindowState.Normal;
            statsWindow.Activate();
            return;
        }
        statsWindow = new StatsWindow(host);
        statsWindow.Closed += (_, _) => statsWindow = null;
        statsWindow.Show();
        statsWindow.Activate();
    }

    NotesWindow? notesWindow;

    public void OpenNotes()
    {
        if (notesWindow != null)
        {
            if (notesWindow.WindowState == WindowState.Minimized) notesWindow.WindowState = WindowState.Normal;
            notesWindow.Activate();
            _ = notesWindow.ReloadAsync();
            return;
        }
        notesWindow = new NotesWindow(host, this);
        notesWindow.Closed += (_, _) => notesWindow = null;
        notesWindow.Show();
        notesWindow.Activate();
    }

    /// <summary>A note was saved or undone somewhere: an open notes window shows it.</summary>
    public void NotesChanged()
    {
        if (notesWindow != null) _ = notesWindow.ReloadAsync();
    }

    void Exit()
    {
        Log.Information("Kullanıcı çıkış yaptı");
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------------ menu

    void BuildMenu()
    {
        MenuItem Item(string header, string glyph, Action click)
        {
            var mi = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13 },
            };
            mi.Click += (_, _) => click();
            return mi;
        }

        miStatus = new MenuItem { Header = "", IsEnabled = false, FontWeight = FontWeights.SemiBold };
        miToday = new MenuItem { Header = "", IsEnabled = false };
        miBreak = Item(L.T("menu.breakNow"), "", () => host.Do(e => e.StartBreakNow()));
        miPause = Item(L.T("menu.pause"), "", () => host.Do(e => e.TogglePause()));
        miReset = Item(L.T("menu.reset"), "", () => host.Do(e => e.Reset()));
        miWidget = Item(L.T("menu.widget"), "", () => SetWidgetVisible(!widget.IsVisible));
        var miStats = Item(L.T("menu.stats"), "", OpenStats);
        var miSettings = Item(L.T("menu.settings"), "", OpenSettings);
        var miExit = Item(L.T("menu.exit"), "", Exit);

        var miNote = Item(L.T("menu.note"), "", OpenNote);
        var miNotes = Item(L.T("notes.open"), "", OpenNotes);
        miAccount = Item(L.T("menu.signIn"), "", () =>
        {
            if (host.Api.IsSignedIn) _ = SignOutAsync(); else OpenLogin();
        });

        menu.Items.Add(miStatus);
        menu.Items.Add(miToday);
        menu.Items.Add(new Separator());
        menu.Items.Add(miNote);
        menu.Items.Add(miNotes);
        menu.Items.Add(new Separator());
        menu.Items.Add(miBreak);
        menu.Items.Add(miPause);
        menu.Items.Add(miReset);
        menu.Items.Add(new Separator());
        menu.Items.Add(miWidget);
        menu.Items.Add(miStats);
        menu.Items.Add(miSettings);
        menu.Items.Add(miAccount);
        menu.Items.Add(new Separator());
        menu.Items.Add(miExit);
        menu.Closed += (_, _) => menuHost.Hide();
    }

    /// <summary>Opens the menu at the mouse. A tiny helper window takes focus so the menu closes on an outside click.</summary>
    public void ShowMenu(Window? _)
    {
        Refresh();
        menuHost.Show();
        menuHost.Activate();
        menu.PlacementTarget = menuHost;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    public void Dispose()
    {
        updateTimer.Stop();
        tray?.Dispose();
        CloseBreakScreens();
    }
}

static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            string? exe = Environment.ProcessPath;
            if (enabled && exe != null) key.SetValue("Eule", $"\"{exe}\"");
            else if (!enabled && key.GetValue("Eule") != null) key.DeleteValue("Eule");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Windows ile başlatma ayarlanamadı");
        }
    }
}
