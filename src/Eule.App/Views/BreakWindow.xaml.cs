using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Eule.App.Theme;
using Eule.Core.Engine;

namespace Eule.App.Views;

/// <summary>
/// Full-screen break. Fades in, the owl sleeps, the ring fills with the break.
/// Strict mode hides every button; only holding Shift for 10 s (if enabled) ends it early.
/// Multi-monitor cover and key blocking arrive in phase 3.
/// </summary>
public partial class BreakWindow : Window
{
    const double EmergencyHoldSeconds = 10;

    readonly AppHost host;
    readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime? shiftDownAt;
    bool closingByCode;

    public BreakWindow(AppHost host)
    {
        this.host = host;
        InitializeComponent();
        Tip.Text = Present.RandomTip();

        SnoozeButton.Click += (_, _) => host.Do(e => e.Snooze());
        SkipButton.Click += (_, _) => host.Do(e => e.Skip());
        StartButton.Click += (_, _) => host.Do(e => e.StartWork());

        holdTimer.Tick += (_, _) => UpdateHold();
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) => { if (host.Engine.IsStrictBreak) Dispatcher.BeginInvoke(Activate, DispatcherPriority.Background); };
        Loaded += (_, _) =>
        {
            var fade = host.Engine.IsStrictBreak ? 2.5 : 0.7;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(fade))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        };
        Refresh();
    }

    public void Refresh()
    {
        var e = host.Engine;
        bool finished = e.Phase == Phase.BreakFinished;

        Owl.Accent = ThemeService.Brush("Eule.Break");
        Owl.Progress = e.Progress;
        Owl.Mood = finished ? Controls.OwlMood.Awake : Controls.OwlMood.Sleeping;

        Heading.Text = finished ? "Mola bitti" : e.IsStrictBreak ? "Şimdi mola zamanı" : "Kalkma zamanı";
        if (finished) Tip.Text = "Hazır olduğunda devam edelim.";
        Time.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        Time.Text = Present.Clock(e.BreakRemaining);

        SnoozeButton.Content = $"{e.Settings.SnoozeMinutes} dk ertele";
        SnoozeButton.Visibility = !finished && e.CanSnooze ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = !finished && e.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        Emergency.Visibility = e.CanEmergencyExit ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var engine = host.Engine;
        if (e.Key == Key.Escape && engine.CanSkip) host.Do(x => x.Skip());
        if (e.Key == Key.Enter && engine.Phase == Phase.BreakFinished) host.Do(x => x.StartWork());
        if (e.Key is Key.LeftShift or Key.RightShift && engine.CanEmergencyExit && shiftDownAt == null)
        {
            shiftDownAt = DateTime.UtcNow;
            holdTimer.Start();
        }
        if (e.Key == Key.System && e.SystemKey == Key.F4 && engine.IsStrictBreak) e.Handled = true;
    }

    void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift) ResetHold();
    }

    void UpdateHold()
    {
        if (shiftDownAt == null || !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
        {
            ResetHold();
            return;
        }
        double held = (DateTime.UtcNow - shiftDownAt.Value).TotalSeconds;
        EmergencyFill.Width = 220 * Math.Min(1, held / EmergencyHoldSeconds);
        if (held >= EmergencyHoldSeconds)
        {
            ResetHold();
            host.Do(x => x.EmergencyExit());
        }
    }

    void ResetHold()
    {
        shiftDownAt = null;
        holdTimer.Stop();
        EmergencyFill.Width = 0;
    }

    public void CloseByCode()
    {
        closingByCode = true;
        holdTimer.Stop();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closingByCode)
        {
            // Alt+F4 / taskbar close: strict cannot be closed, otherwise treat it as "skip" or "start".
            // The shell closes the window once the engine has moved on; run the command after this event
            // because calling Close() while the window is closing throws.
            e.Cancel = true;
            var engine = host.Engine;
            if (!engine.IsStrictBreak)
            {
                Action<BreakEngine> cmd = engine.Phase == Phase.BreakFinished ? x => x.StartWork() : x => x.Skip();
                Dispatcher.BeginInvoke(() => host.Do(cmd));
            }
        }
        base.OnClosing(e);
    }
}
