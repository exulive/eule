using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Eule.App.Theme;
using Eule.Core.Localization;
using Eule.Core.Settings;

namespace Eule.App.Views;

/// <summary>First launch: meet Eule, pick a rhythm, pick a reminder style. Three steps, then out of the way.</summary>
public partial class WelcomeWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly AppHost host;
    readonly FrameworkElement[] steps;
    int step;

    public WelcomeWindow(AppHost host)
    {
        this.host = host;
        InitializeComponent();
        steps = [Step1, Step2, Step3];
        Owl.Accent = ThemeService.Brush("Eule.Sit");

        var s = host.Settings;
        foreach (RadioButton rb in Rhythms.Children)
            rb.IsChecked = (string)rb.Tag == $"{s.WorkMinutes},{s.BreakMinutes}";
        if (!Rhythms.Children.OfType<RadioButton>().Any(r => r.IsChecked == true))
            ((RadioButton)Rhythms.Children[0]).IsChecked = true;
        foreach (RadioButton rb in Levels.Children)
            rb.IsChecked = (string)rb.Tag == s.Level.ToString();

        for (int i = 0; i < steps.Length; i++)
        {
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
            Dots.Children.Add(dot);
        }

        BackButton.Click += (_, _) => Go(step - 1);
        NextButton.Click += (_, _) =>
        {
            if (step < steps.Length - 1) Go(step + 1);
            else Finish();
        };
        Go(0);
    }

    void Go(int next)
    {
        if (next < 0 || next >= steps.Length) return;
        steps[step].Visibility = Visibility.Collapsed;
        step = next;
        var el = steps[step];
        el.Visibility = Visibility.Visible;
        el.Opacity = 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)));

        BackButton.Visibility = step == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = L.T(step == steps.Length - 1 ? "welcome.done" : "welcome.next");
        for (int i = 0; i < Dots.Children.Count; i++)
            ((Ellipse)Dots.Children[i]).SetResourceReference(Shape.FillProperty, i == step ? "Eule.Ink" : "Eule.Track");
    }

    void Finish()
    {
        var s = host.Settings.Clone();
        if (Rhythms.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string rhythm)
        {
            var p = rhythm.Split(',');
            s.WorkMinutes = int.Parse(p[0]);
            s.BreakMinutes = int.Parse(p[1]);
        }
        if (Levels.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag is string level)
            s.Level = Enum.Parse<ReminderLevel>(level);
        s.StartWithWindows = StartupSwitch.IsChecked == true;
        s.ShowWidget = true;
        host.SaveSettings(s);
        host.Do(e => e.Reset()); // start the first sitting period with the chosen rhythm
        Close();
    }
}
