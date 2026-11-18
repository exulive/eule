using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Eule.App.Theme;
using Eule.Core;
using Eule.Core.Insights;
using Eule.Core.Localization;
using Eule.Core.Settings;

namespace Eule.App.Views;

/// <summary>One calm page of settings. Every change is saved on its own (no Save button).</summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly AppHost host;
    readonly UpdateService updates;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    bool loading;
    Velopack.UpdateInfo? pendingUpdate;

    public event Action? OpenStatsRequested;

    public SettingsWindow(AppHost host, UpdateService updates)
    {
        this.host = host;
        this.updates = updates;
        InitializeComponent();
        BuildDayChips();

        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };

        foreach (var nb in new[] { WorkBox, BreakBox, GoalBox, SnoozeBox, MaxSnoozeBox, WarnBox })
            nb.ValueChanged += (_, _) => Changed();
        foreach (var ts in new[] { EmergencySwitch, BusySwitch, AwaySwitch, AutoRestartSwitch, EyeSwitch, CoverSwitch, SoundSwitch, WidgetSwitch, StartupSwitch, HoursSwitch })
            ts.Click += (_, _) => Changed();
        foreach (RadioButton rb in Levels.Children)
            rb.Checked += (_, _) => Changed();
        foreach (ToggleButton chip in Presets.Children)
            chip.Click += (_, _) => ApplyPreset((string)chip.Tag);
        foreach (ToggleButton day in Days.Children)
            day.Click += (_, _) => Changed();
        foreach (ToggleButton t in Themes.Children)
            t.Click += (_, _) => Select(Themes, (string)t.Tag);
        foreach (ToggleButton t in Languages.Children)
            t.Click += (_, _) => Select(Languages, (string)t.Tag);
        HoursStart.LostFocus += (_, _) => Changed();
        HoursEnd.LostFocus += (_, _) => Changed();
        OpenFolder.Click += (_, _) => Process.Start("explorer.exe", AppPaths.Default.DataDir);
        StatsButton.Click += (_, _) => OpenStatsRequested?.Invoke();
        RestartButton.Click += (_, _) => { saveTimer.Stop(); Save(); App.Restart(); };
        UpdateButton.Click += async (_, _) => await OnUpdateClick();

        VersionText.Text = L.F("set.version", UpdateService.CurrentVersion);
        UpdateText.Text = updates.CanUpdate ? "" : L.T(updates.HasFeed ? "set.updateOff" : "set.updateNoFeed");
        UpdateButton.Visibility = updates.CanUpdate ? Visibility.Visible : Visibility.Collapsed;

        host.Updated += RefreshHeader;
        Closed += (_, _) =>
        {
            host.Updated -= RefreshHeader;
            if (saveTimer.IsEnabled) { saveTimer.Stop(); Save(); }
        };

        SyncFromSettings();
        RefreshHeader();
    }

    void BuildDayChips()
    {
        var names = Words.DayShort;
        for (int i = 0; i < 7; i++)
        {
            var dow = (DayOfWeek)((i + 1) % 7); // Monday first
            Days.Children.Add(new ToggleButton
            {
                Style = (Style)FindResource("Eule.Chip"), Content = names[i], Tag = ((int)dow).ToString(),
            });
        }
    }

    /// <summary>Loads every control from the current settings (also used when the widget is toggled from the tray).</summary>
    public void SyncFromSettings()
    {
        loading = true;
        var s = host.Settings;
        WorkBox.Value = s.WorkMinutes;
        BreakBox.Value = s.BreakMinutes;
        GoalBox.Value = s.DailyBreakGoal;
        SnoozeBox.Value = s.SnoozeMinutes;
        MaxSnoozeBox.Value = s.MaxSnoozes;
        WarnBox.Value = s.WarnSeconds;
        foreach (RadioButton rb in Levels.Children) rb.IsChecked = (string)rb.Tag == s.Level.ToString();
        EmergencySwitch.IsChecked = s.EmergencyExit;
        BusySwitch.IsChecked = s.DeferWhenBusy;
        AwaySwitch.IsChecked = s.IdleCountsAsBreak;
        AutoRestartSwitch.IsChecked = s.AutoRestart;
        EyeSwitch.IsChecked = s.EyeBreak.Enabled;
        CoverSwitch.IsChecked = s.CoverAllMonitors;
        SoundSwitch.IsChecked = s.Sound;
        WidgetSwitch.IsChecked = s.ShowWidget;
        StartupSwitch.IsChecked = s.StartWithWindows;
        HoursSwitch.IsChecked = s.WorkHours.Enabled;
        HoursStart.Text = s.WorkHours.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        HoursEnd.Text = s.WorkHours.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        foreach (ToggleButton day in Days.Children)
            day.IsChecked = s.WorkHours.Days.Contains((DayOfWeek)int.Parse((string)day.Tag));
        foreach (ToggleButton t in Themes.Children) t.IsChecked = (string)t.Tag == s.Theme.ToString();
        foreach (ToggleButton t in Languages.Children) t.IsChecked = (string)t.Tag == s.Language;
        loading = false;
        UpdateDependentRows();
    }

    void RefreshHeader()
    {
        var e = host.Engine;
        Owl.Accent = ThemeService.AccentFor(e);
        Owl.Progress = e.Progress;
        Owl.Mood = Present.Mood(e);
        var t = host.Stats.Today;
        var streak = host.Streak;
        TodayText.Text = L.F("set.today", Present.Status(e), Present.Duration(t.SitSeconds), t.AllBreaks, streak.Goal) +
                         (streak.Days > 0 ? L.F("menu.streak", streak.Days) : "");
    }

    void ApplyPreset(string tag)
    {
        var parts = tag.Split(',');
        loading = true;
        WorkBox.Value = int.Parse(parts[0]);
        BreakBox.Value = int.Parse(parts[1]);
        loading = false;
        Changed();
    }

    void Select(Panel group, string tag)
    {
        foreach (ToggleButton t in group.Children) t.IsChecked = (string)t.Tag == tag;
        Changed();
    }

    void Changed()
    {
        if (loading) return;
        UpdateDependentRows();
        saveTimer.Stop();
        saveTimer.Start();
    }

    string? Selected(Panel group) => group.Children.OfType<ToggleButton>().FirstOrDefault(t => t.IsChecked == true)?.Tag as string;

    void UpdateDependentRows()
    {
        string? level = Levels.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        bool strict = level == nameof(ReminderLevel.Strict);
        EmergencyRow.Visibility = strict ? Visibility.Visible : Visibility.Collapsed;
        SnoozeRow.Visibility = strict ? Visibility.Collapsed : Visibility.Visible;
        HoursPanel.Visibility = HoursSwitch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RestartRow.Visibility = Selected(Languages) is string lang && lang != L.Lang ? Visibility.Visible : Visibility.Collapsed;

        int work = (int)(WorkBox.Value ?? 0), brk = (int)(BreakBox.Value ?? 0);
        foreach (ToggleButton chip in Presets.Children)
            chip.IsChecked = (string)chip.Tag == $"{work},{brk}";
    }

    void Save()
    {
        var s = host.Settings.Clone();
        s.WorkMinutes = (int)(WorkBox.Value ?? s.WorkMinutes);
        s.BreakMinutes = (int)(BreakBox.Value ?? s.BreakMinutes);
        s.DailyBreakGoal = (int)(GoalBox.Value ?? s.DailyBreakGoal);
        s.SnoozeMinutes = (int)(SnoozeBox.Value ?? s.SnoozeMinutes);
        s.MaxSnoozes = (int)(MaxSnoozeBox.Value ?? s.MaxSnoozes);
        s.WarnSeconds = (int)(WarnBox.Value ?? s.WarnSeconds);
        if (Levels.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string level)
            s.Level = Enum.Parse<ReminderLevel>(level);
        s.EmergencyExit = EmergencySwitch.IsChecked == true;
        s.DeferWhenBusy = BusySwitch.IsChecked == true;
        s.IdleCountsAsBreak = AwaySwitch.IsChecked == true;
        s.AutoRestart = AutoRestartSwitch.IsChecked == true;
        s.EyeBreak.Enabled = EyeSwitch.IsChecked == true;
        s.CoverAllMonitors = CoverSwitch.IsChecked == true;
        s.Sound = SoundSwitch.IsChecked == true;
        s.ShowWidget = WidgetSwitch.IsChecked == true;
        s.StartWithWindows = StartupSwitch.IsChecked == true;
        s.WorkHours.Enabled = HoursSwitch.IsChecked == true;
        if (TryTime(HoursStart.Text, out var start)) s.WorkHours.Start = start;
        if (TryTime(HoursEnd.Text, out var end)) s.WorkHours.End = end;
        s.WorkHours.Days = Days.Children.OfType<ToggleButton>().Where(d => d.IsChecked == true)
            .Select(d => (DayOfWeek)int.Parse((string)d.Tag)).ToList();
        if (Selected(Themes) is string theme) s.Theme = Enum.Parse<AppTheme>(theme);
        if (Selected(Languages) is string lang) s.Language = lang;

        host.SaveSettings(s);

        // Show the normalised values back (e.g. an unparsable time snaps back).
        loading = true;
        HoursStart.Text = host.Settings.WorkHours.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        HoursEnd.Text = host.Settings.WorkHours.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        loading = false;
    }

    async Task OnUpdateClick()
    {
        if (pendingUpdate != null)
        {
            updates.ApplyAndRestart(pendingUpdate);
            return;
        }
        UpdateButton.IsEnabled = false;
        UpdateText.Text = L.T("set.updateChecking");
        var (info, error) = await updates.CheckAndDownloadAsync();
        UpdateButton.IsEnabled = true;
        if (error) UpdateText.Text = L.T("set.updateError");
        else if (info == null) UpdateText.Text = L.T("set.updateNone");
        else
        {
            pendingUpdate = info;
            UpdateText.Text = L.F("set.updateFound", info.TargetFullRelease.Version);
            UpdateButton.Content = L.T("set.updateInstall");
        }
    }

    static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm", "H.mm", "HH.mm", "HHmm"], CultureInfo.InvariantCulture,
            DateTimeStyles.None, out time);
}
