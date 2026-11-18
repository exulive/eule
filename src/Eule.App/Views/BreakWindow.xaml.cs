using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Eule.App.Interop;
using Eule.App.Theme;
using Eule.Core.Engine;
using Forms = System.Windows.Forms;

namespace Eule.App.Views;

/// <summary>
/// Full-screen break on the primary monitor. A breathing halo (4 s in, 4 s out) around the sleeping owl,
/// one small exercise at a time, and the break clock. Strict mode dims slowly for 10 s, hides every button,
/// blocks the shortcuts that would leave the screen and keeps itself in front; only holding Shift ends it early.
/// </summary>
public partial class BreakWindow : Window
{
    const double EmergencyHoldSeconds = 10;
    public const double StrictDimSeconds = 10;
    static readonly TimeSpan BreathHalf = TimeSpan.FromSeconds(4);

    readonly AppHost host;
    readonly bool strict;
    readonly DispatcherTimer holdTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly DispatcherTimer breathTimer = new() { Interval = BreathHalf };
    readonly DispatcherTimer exerciseTimer = new();
    readonly List<Present.Exercise> plan = Present.ExercisePlan();
    KeyboardBlocker? blocker;
    DateTime? shiftDownAt;
    bool closingByCode, dimming, breathingIn = true;
    int exerciseIndex;

    public BreakWindow(AppHost host, Forms.Screen screen)
    {
        this.host = host;
        strict = host.Engine.IsStrictBreak;
        InitializeComponent();
        ScreenPlacement.CoverScreen(this, screen);

        SnoozeButton.Click += (_, _) => host.Do(e => e.Snooze());
        SkipButton.Click += (_, _) => host.Do(e => e.Skip());
        StartButton.Click += (_, _) => host.Do(e => e.StartWork());

        holdTimer.Tick += (_, _) => UpdateHold();
        breathTimer.Tick += (_, _) => { breathingIn = !breathingIn; BreathText.Text = breathingIn ? "nefes al" : "ver"; };
        exerciseTimer.Tick += (_, _) => ShowExercise(exerciseIndex + 1);
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        Deactivated += (_, _) =>
        {
            if (host.Engine.IsStrictBreak) Dispatcher.BeginInvoke(Activate, DispatcherPriority.Background);
        };
        Loaded += (_, _) => OnLoaded();

        // Spread the exercises over the break: 30–45 s each.
        int count = Math.Clamp(host.Engine.BreakTotal / 35, 1, plan.Count);
        exerciseTimer.Interval = TimeSpan.FromSeconds(Math.Max(20, host.Engine.BreakTotal / (double)count));
        BuildDots(count);
        ShowExercise(0, animate: false);
        Refresh();
    }

    void OnLoaded()
    {
        if (strict)
        {
            blocker = new KeyboardBlocker();
            dimming = true;
            Refresh();
        }
        // Strict: a steady (linear) dim so the "screen is going dark" warning is readable from the first seconds.
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(strict ? StrictDimSeconds : 0.7))
        {
            EasingFunction = strict ? null : new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fade.Completed += (_, _) => { dimming = false; Refresh(); };
        BeginAnimation(OpacityProperty, fade);

        var breath = new DoubleAnimation(0.8, 1.0, BreathHalf)
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, breath);
        HaloScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, breath);
        breathTimer.Start();
        exerciseTimer.Start();
        Activate();
    }

    public void Refresh()
    {
        var e = host.Engine;
        bool finished = e.Phase == Phase.BreakFinished;

        Owl.Accent = ThemeService.Brush("Eule.Break");
        Owl.Progress = e.Progress;
        Owl.Mood = finished ? Controls.OwlMood.Awake : Controls.OwlMood.Sleeping;

        if (dimming)
        {
            Heading.Text = "Ekran kararıyor";
            Sub.Text = "Kaydını bitir. Birazdan mola başlıyor.";
        }
        else if (finished)
        {
            Heading.Text = "Mola bitti";
            Sub.Text = "Hazır olduğunda devam edelim.";
        }
        else
        {
            Heading.Text = strict ? "Şimdi mola zamanı" : "Kalkma zamanı";
        }
        Sub.Visibility = dimming || finished ? Visibility.Visible : Visibility.Collapsed;
        ExerciseCard.Visibility = dimming || finished ? Visibility.Collapsed : Visibility.Visible;
        BreathText.Visibility = finished ? Visibility.Hidden : Visibility.Visible;
        Time.Visibility = finished ? Visibility.Collapsed : Visibility.Visible;
        Time.Text = Present.Clock(e.BreakRemaining);

        SnoozeButton.Content = $"{e.Settings.SnoozeMinutes} dk ertele";
        SnoozeButton.Visibility = !finished && e.CanSnooze ? Visibility.Visible : Visibility.Collapsed;
        SkipButton.Visibility = !finished && e.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
        Emergency.Visibility = e.CanEmergencyExit && !dimming ? Visibility.Visible : Visibility.Collapsed;

        if (finished)
        {
            exerciseTimer.Stop();
            breathTimer.Stop();
            blocker?.Dispose();
            blocker = null;
        }
    }

    // ------------------------------------------------------------------ exercises

    void BuildDots(int count)
    {
        ExerciseDots.Children.Clear();
        for (int i = 0; i < count; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Margin = new Thickness(3, 0, 0, 0) };
            dot.SetResourceReference(Shape.FillProperty, "Eule.Track");
            ExerciseDots.Children.Add(dot);
        }
    }

    void ShowExercise(int index, bool animate = true)
    {
        int count = ExerciseDots.Children.Count;
        if (index >= count) { exerciseTimer.Stop(); return; }
        exerciseIndex = index;

        void Apply()
        {
            ExerciseTitle.Text = plan[index].Title;
            ExerciseText.Text = plan[index].Text;
            for (int i = 0; i < count; i++)
                ((Ellipse)ExerciseDots.Children[i]).SetResourceReference(Shape.FillProperty, i <= index ? "Eule.Break" : "Eule.Track");
        }

        if (!animate) { Apply(); return; }
        var outAnim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        outAnim.Completed += (_, _) =>
        {
            Apply();
            ExerciseCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)));
        };
        ExerciseCard.BeginAnimation(OpacityProperty, outAnim);
    }

    // ------------------------------------------------------------------ keys

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var engine = host.Engine;
        if (e.Key == Key.Escape && engine.CanSkip) host.Do(x => x.Skip());
        if (e.Key == Key.Enter && engine.Phase == Phase.BreakFinished) host.Do(x => x.StartWork());
        if (e.Key is Key.LeftShift or Key.RightShift && engine.CanEmergencyExit && !dimming && shiftDownAt == null)
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

    // ------------------------------------------------------------------ closing

    public void CloseByCode()
    {
        closingByCode = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!closingByCode)
        {
            // Alt+F4 / taskbar close. The shell closes the window once the engine has moved on;
            // run the command after this event because calling Close() while closing throws.
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

    protected override void OnClosed(EventArgs e)
    {
        holdTimer.Stop();
        breathTimer.Stop();
        exerciseTimer.Stop();
        blocker?.Dispose();
        base.OnClosed(e);
    }
}
