using System.Windows;
using System.Windows.Media;
using Eule.Core.Engine;
using Eule.Core.Settings;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace Eule.App.Theme;

/// <summary>Swaps the Eule token dictionary (and WPF-UI's theme) between light and dark, following Windows when set to System.</summary>
public sealed class ThemeService
{
    static readonly Uri LightUri = new("pack://application:,,,/Eule;component/Theme/Light.xaml");
    static readonly Uri DarkUri = new("pack://application:,,,/Eule;component/Theme/Dark.xaml");

    AppTheme preference = AppTheme.System;
    ResourceDictionary? current;

    public bool IsDark { get; private set; }
    public event Action? Changed;

    public ThemeService()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && preference == AppTheme.System)
                Application.Current.Dispatcher.BeginInvoke(() => Apply(preference));
        };
    }

    public void Apply(AppTheme theme)
    {
        preference = theme;
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => SystemPrefersDark(),
        };
        if (current != null && dark == IsDark) return;
        IsDark = dark;

        var dict = new ResourceDictionary { Source = dark ? DarkUri : LightUri };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (current != null) merged.Remove(current);
        merged.Add(dict);
        current = dict;

        ApplicationThemeManager.Apply(dark ? ApplicationTheme.Dark : ApplicationTheme.Light);
        Changed?.Invoke();
    }

    static bool SystemPrefersDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The single accent colour that tells the state at a glance.</summary>
    public static Brush AccentFor(BreakEngine e) => Brush(AccentKey(e));

    public static string AccentKey(BreakEngine e) => e.Phase switch
    {
        Phase.Break or Phase.BreakFinished or Phase.Away => "Eule.Break",
        Phase.Paused or Phase.OffHours => "Eule.Idle",
        Phase.BreakDue or Phase.Deferred => "Eule.Warn",
        _ => e.WorkLeft <= Math.Max(e.Settings.WarnSeconds, e.WorkTarget / 10) ? "Eule.Warn" : "Eule.Sit",
    };
}
