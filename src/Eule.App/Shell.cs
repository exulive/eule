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
    SettingsWindow? settingsWindow;

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
        if (widget.IsVisible) widget.Refresh();

        bool breakScreen = e.Phase is Phase.Break || (e.Phase is Phase.BreakFinished && breakWindow != null);
        if (breakScreen && breakWindow == null)
        {
            breakWindow = new BreakWindow(host);
            breakWindow.Closed += (_, _) => breakWindow = null;
            breakWindow.Show();
            breakWindow.Activate();
        }
        else if (!breakScreen && breakWindow != null)
        {
            breakWindow.CloseByCode();
        }
        breakWindow?.Refresh();

        miStatus.Header = status;
        var t = host.Stats.Today;
        miToday.Header = $"Bugün {Present.Duration(t.SitSeconds)} oturdun · {t.AllBreaks} mola";
        miBreak.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue;
        miPause.Header = e.Phase == Phase.Paused ? "Devam et" : "Duraklat";
        miPause.IsEnabled = e.Phase is Phase.Working or Phase.Paused or Phase.Deferred or Phase.BreakDue or Phase.Away;
        miReset.IsEnabled = e.Phase is not Phase.Break;
        miWidget.IsChecked = widget.IsVisible;
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
                if (!widget.IsVisible) tray.Notify("Kalkma zamanı", "Biraz kalkıp dolaşmaya ne dersin?");
                break;
            case WarningRaised w when !widget.IsVisible:
                tray.Notify("Mola yaklaşıyor", $"{Math.Max(1, w.SecondsLeft / 60)} dakika sonra kalkma zamanı.");
                break;
            case BreakEnded { Reason: BreakEndReason.Completed }:
                Play(SystemSounds.Asterisk);
                break;
            case EyeBreakDue eye:
                tray.Notify("Göz molası", $"{eye.Seconds} saniye boyunca uzağa bak.");
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
        breakWindow?.CloseByCode();
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
