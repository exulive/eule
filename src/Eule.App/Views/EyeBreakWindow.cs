using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Eule.App.Controls;
using Eule.App.Theme;

namespace Eule.App.Views;

/// <summary>
/// The 20-20-20 reminder: a small pill at the top of the screen for the duration of the eye break.
/// Click-through and never takes focus, so it can be ignored while typing.
/// </summary>
sealed class EyeBreakWindow : Window
{
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly OwlView owl = new() { Width = 34, Height = 34, Mood = OwlMood.Awake };
    readonly TextBlock count = new() { FontSize = 15, Margin = new Thickness(12, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly int total;
    int left;

    public EyeBreakWindow(int seconds)
    {
        total = left = seconds;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        count.SetResourceReference(StyleProperty, "Eule.Clock");

        var text = new TextBlock
        {
            Text = "Gözlerini dinlendir: uzaktaki bir noktaya bak",
            FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Eule.Ink");
        owl.Accent = ThemeService.Brush("Eule.Break");

        var card = new Border
        {
            CornerRadius = new CornerRadius(26),
            Padding = new Thickness(8, 8, 18, 8),
            Margin = new Thickness(16),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { owl, text, count } },
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 4, Direction = 270, Opacity = 0.18 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Eule.Raised");
        card.SetResourceReference(Border.BorderBrushProperty, "Eule.Line");
        Content = card;

        timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => MakeClickThrough();
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 12;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(400)));
            timer.Start();
        };
        Update();
    }

    void Tick()
    {
        left--;
        Update();
        if (left > 0) return;
        timer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(500));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    void Update()
    {
        count.Text = left.ToString();
        owl.Progress = 1 - (double)left / total;
    }

    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint h, int i, nint v);

    void MakeClickThrough()
    {
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(h, GWL_EXSTYLE, GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }
}
