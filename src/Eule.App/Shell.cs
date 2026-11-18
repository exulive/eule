using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Eule.App.Theme;
using Eule.App.Views;
using Eule.Core.Engine;
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
    MenuItem miStatus = null!, miToday = null!, miBreak = null!, miPause = null!, miReset = null!, miWidget = null!;

    public Shell(AppHost host) => this.host = host;

    public void Start()
    {
        theme.Apply(host.Settings.Theme);
        BuildMenu();

        tray = new TrayIcon();
        tray.LeftClick += () => SetWidgetVisible(true);
        tray.DoubleClick += OpenSettings;
        tray.RightClick += () => ShowMenu(null);

        widget = new WidgetWindow(host, this);
        if (host.Settings.ShowWidget) widget.Show();

        host.Updated += Refresh;
        host.SettingsChanged += OnSettingsChanged;
        host.Engine.Raised += OnEngineEvent;
        theme.Changed += Refresh;
        if (host.Streak.TodayMet) goalCelebrated = host.Stats.Today.Date; // don't congratulate again after a restart

        StartupRegistration.Apply(host.Settings.StartWithWindows);
        Refresh();

        if (host.FirstRun) OpenSettings();
        else if (!host.Settings.ShowWidget)
            tray.Notify("Eule burada", "Simgeye tıklayarak Eule'yi köşede gösterebilirsin.");
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
        miToday.Header = $"Bugün {Present.Duration(t.SitSeconds)} · {t.AllBreaks}/{streak.Goal} mola" +
                         (streak.Days > 0 ? $" · {streak.Days} gün seri" : "");
        miBreak.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue;
        miPause.Header = e.Phase == Phase.Paused ? "Devam et" : "Duraklat";
        miPause.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue or Phase.Away;
        miReset.IsEnabled = e.Phase is not Phase.Break;
        miWidget.IsChecked = widget.IsVisible;
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
        tray.Notify("Bugünkü hedef tamam",
            streak.Days > 1 ? $"{streak.Days} gündür hedeftesin. Eule seninle gurur duyuyor." : "Eule seninle gurur duyuyor.");
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
                tray.Notify("Mola yaklaşıyor", $"{Math.Max(1, w.SecondsLeft / 60)} dakika sonra kalkma zamanı.");
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
        settingsWindow = new SettingsWindow(host);
        settingsWindow.Closed += (_, _) => settingsWindow = null;
        settingsWindow.Show();
        settingsWindow.Activate();
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
        miBreak = Item("Şimdi mola ver", "", () => host.Do(e => e.StartBreakNow()));
        miPause = Item("Duraklat", "", () => host.Do(e => e.TogglePause()));
        miReset = Item("Sayacı sıfırla", "", () => host.Do(e => e.Reset()));
        miWidget = Item("Köşede göster", "", () => SetWidgetVisible(!widget.IsVisible));
        var miSettings = Item("Ayarlar", "", OpenSettings);
        var miExit = Item("Çıkış", "", Exit);

        menu.Items.Add(miStatus);
        menu.Items.Add(miToday);
        menu.Items.Add(new Separator());
        menu.Items.Add(miBreak);
        menu.Items.Add(miPause);
        menu.Items.Add(miReset);
        menu.Items.Add(new Separator());
        menu.Items.Add(miWidget);
        menu.Items.Add(miSettings);
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
