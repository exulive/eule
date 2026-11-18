using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Eule.App.Controls;
using Eule.App.Interop;
using Eule.App.Theme;
using Eule.Core.Engine;
using Forms = System.Windows.Forms;

namespace Eule.App.Views;

/// <summary>Quiet cover for the other monitors during a break: a small sleeping owl and the clock.</summary>
sealed class CoverWindow : Window
{
    readonly AppHost host;
    readonly OwlView owl = new() { Width = 96, Height = 96, Mood = OwlMood.Sleeping };
    readonly TextBlock time = new() { FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
    bool closingByCode;

    public CoverWindow(AppHost host, Forms.Screen screen, double fadeSeconds)
    {
        this.host = host;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Opacity = 0;
        SetResourceReference(BackgroundProperty, "Eule.Overlay");
        time.SetResourceReference(StyleProperty, "Eule.Clock");
        ScreenPlacement.CoverScreen(this, screen);

        Content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { owl, time },
        };
        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(fadeSeconds)));
        Refresh();
    }

    public void Refresh()
    {
        var e = host.Engine;
        owl.Accent = ThemeService.Brush("Eule.Break");
        owl.Progress = e.Progress;
        owl.Mood = e.Phase == Phase.BreakFinished ? OwlMood.Awake : OwlMood.Sleeping;
        time.Text = e.Phase == Phase.BreakFinished ? "" : Present.Clock(e.BreakRemaining);
    }

    public void CloseByCode()
    {
        closingByCode = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closingByCode) e.Cancel = true;
        base.OnClosing(e);
    }
}
