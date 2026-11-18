using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Eule.App.Controls;

public enum OwlMood
{
    /// <summary>Eyes open, blinks now and then.</summary>
    Awake,
    /// <summary>Big pupils, small hop: time to stand up.</summary>
    Alert,
    /// <summary>Half-closed eyes: paused / off hours.</summary>
    Drowsy,
    /// <summary>Eyes closed, slow breathing: on a break.</summary>
    Sleeping,
}

/// <summary>
/// Eule's mascot: a minimal owl inside a progress ring. Drawn on a 100×100 canvas and scaled with a Viewbox,
/// so the same drawing serves the 44 px widget and the 180 px break screen.
/// </summary>
public sealed class OwlView : Viewbox
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(OwlView),
        new PropertyMetadata(0.0, (d, _) => ((OwlView)d).UpdateArc()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(OwlView),
        new PropertyMetadata(Brushes.SeaGreen, (d, e) => ((OwlView)d).OnAccent((Brush)e.NewValue)));

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(OwlMood), typeof(OwlView),
        new PropertyMetadata(OwlMood.Awake, (d, _) => ((OwlView)d).ApplyMood()));

    public static readonly DependencyProperty ShowRingProperty = DependencyProperty.Register(
        nameof(ShowRing), typeof(bool), typeof(OwlView),
        new PropertyMetadata(true, (d, e) => ((OwlView)d).ring.Visibility = ((OwlView)d).track.Visibility =
            (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public OwlMood Mood { get => (OwlMood)GetValue(MoodProperty); set => SetValue(MoodProperty, value); }
    public bool ShowRing { get => (bool)GetValue(ShowRingProperty); set => SetValue(ShowRingProperty, value); }

    const double RingWidth = 5;
    readonly Ellipse track;
    readonly Path ring, beak;
    readonly Canvas openEyes, closedEyes, owl;
    readonly ScaleTransform eyeScale = new(1, 1, 50, 48);
    readonly List<ScaleTransform> pupilScales = [];
    readonly ScaleTransform breathe = new(1, 1, 50, 58);
    readonly TranslateTransform hop = new();
    readonly TextBlock zzz;
    readonly DispatcherTimer blinkTimer = new();
    static readonly Random Rng = new();

    public OwlView()
    {
        Stretch = Stretch.Uniform;
        var canvas = new Canvas { Width = 100, Height = 100 };

        track = new Ellipse
        {
            Width = 100 - RingWidth, Height = 100 - RingWidth, StrokeThickness = RingWidth,
        };
        Canvas.SetLeft(track, RingWidth / 2);
        Canvas.SetTop(track, RingWidth / 2);
        track.SetResourceReference(Shape.StrokeProperty, "Eule.Track");

        ring = new Path
        {
            StrokeThickness = RingWidth, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };

        owl = new Canvas { Width = 100, Height = 100 };
        owl.RenderTransform = new TransformGroup { Children = { breathe, hop } };

        // Head with two ear tufts; one closed path so it reads as a single shape.
        var head = new Path
        {
            Data = Geometry.Parse("M 25,42 L 27,21 L 40,31 A 26,26 0 0 1 60,31 L 73,21 L 75,42 A 26,26 0 1 1 25,42 Z"),
            StrokeLineJoin = PenLineJoin.Round,
        };
        head.SetResourceReference(Shape.FillProperty, "Eule.OwlBody");

        openEyes = new Canvas { RenderTransform = eyeScale };
        foreach (double cx in new[] { 40.0, 60.0 })
        {
            openEyes.Children.Add(Circle(cx, 48, 8.5, "Eule.OwlEye"));
            var pupil = Circle(cx, 48, 4.2, "Eule.OwlBody");
            var scale = new ScaleTransform(1, 1, 4.2, 4.2); // centre of the pupil's own box
            pupilScales.Add(scale);
            pupil.RenderTransform = scale;
            openEyes.Children.Add(pupil);
            openEyes.Children.Add(Circle(cx + 1.6, 46.4, 1.3, "Eule.OwlEye"));
        }

        closedEyes = new Canvas { Visibility = Visibility.Collapsed };
        foreach (double cx in new[] { 40.0, 60.0 })
        {
            var lid = new Path
            {
                Data = Geometry.Parse($"M {cx - 7},48 Q {cx},54 {cx + 7},48"),
                StrokeThickness = 2.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            lid.SetResourceReference(Shape.StrokeProperty, "Eule.OwlEye");
            closedEyes.Children.Add(lid);
        }

        beak = new Path { Data = Geometry.Parse("M 50,56 L 46.5,60.5 L 50,66 L 53.5,60.5 Z") };

        zzz = new TextBlock
        {
            Text = "z", FontSize = 13, FontWeight = FontWeights.SemiBold, Opacity = 0,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };
        zzz.SetResourceReference(TextBlock.ForegroundProperty, "Eule.Muted");
        Canvas.SetLeft(zzz, 71);
        Canvas.SetTop(zzz, 18);

        owl.Children.Add(head);
        owl.Children.Add(openEyes);
        owl.Children.Add(closedEyes);
        owl.Children.Add(beak);

        canvas.Children.Add(track);
        canvas.Children.Add(ring);
        canvas.Children.Add(owl);
        canvas.Children.Add(zzz);
        Child = canvas;

        OnAccent(Accent);
        UpdateArc();

        blinkTimer.Tick += (_, _) => Blink();
        Loaded += (_, _) => ApplyMood();
        Unloaded += (_, _) => blinkTimer.Stop();
    }

    static Ellipse Circle(double cx, double cy, double r, string brushKey)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2 };
        Canvas.SetLeft(e, cx - r);
        Canvas.SetTop(e, cy - r);
        e.SetResourceReference(Shape.FillProperty, brushKey);
        return e;
    }

    void OnAccent(Brush b)
    {
        ring.Stroke = b;
        beak.Fill = b;
    }

    void UpdateArc()
    {
        double p = Math.Clamp(Progress, 0, 1);
        double r = (100 - RingWidth) / 2;
        const double c = 50;
        if (p <= 0.0005) { ring.Data = null; return; }
        if (p >= 0.9995) { ring.Data = new EllipseGeometry(new Point(c, c), r, r); return; }
        double a = p * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(a), c - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = new Point(c, c - r), IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, p > 0.5, SweepDirection.Clockwise, true));
        ring.Data = new PathGeometry([fig]);
    }

    // ------------------------------------------------------------------ moods

    void ApplyMood()
    {
        if (!IsLoaded) return;
        breathe.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        breathe.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        hop.BeginAnimation(TranslateTransform.YProperty, null);
        zzz.BeginAnimation(OpacityProperty, null);
        eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        blinkTimer.Stop();

        bool sleeping = Mood == OwlMood.Sleeping;
        openEyes.Visibility = sleeping ? Visibility.Collapsed : Visibility.Visible;
        closedEyes.Visibility = sleeping ? Visibility.Visible : Visibility.Collapsed;
        eyeScale.ScaleY = Mood == OwlMood.Drowsy ? 0.42 : 1;
        foreach (var s in pupilScales) Animate(s, Mood == OwlMood.Alert ? 1.25 : 1, 260);
        zzz.Opacity = 0;

        switch (Mood)
        {
            case OwlMood.Awake:
                ScheduleBlink();
                break;

            case OwlMood.Alert:
                var hopAnim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(2.4) };
                hopAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                hopAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-4, KeyTime.FromPercent(0.08), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                hopAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.18), new BounceEase { Bounces = 1, Bounciness = 3 }));
                hopAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                hop.BeginAnimation(TranslateTransform.YProperty, hopAnim);
                ScheduleBlink();
                break;

            case OwlMood.Sleeping:
                var breath = new DoubleAnimation(1, 1.035, TimeSpan.FromSeconds(2))
                {
                    AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                };
                breathe.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
                breathe.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
                var z = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, Duration = TimeSpan.FromSeconds(4) };
                z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
                z.KeyFrames.Add(new LinearDoubleKeyFrame(0.9, KeyTime.FromPercent(0.3)));
                z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0.7)));
                z.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
                zzz.BeginAnimation(OpacityProperty, z);
                break;
        }
    }

    void ScheduleBlink()
    {
        blinkTimer.Interval = TimeSpan.FromMilliseconds(Rng.Next(3500, 7500));
        blinkTimer.Start();
    }

    void Blink()
    {
        blinkTimer.Stop();
        if (Mood is not (OwlMood.Awake or OwlMood.Alert)) return;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(170) };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0.08, KeyTime.FromPercent(0.45)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1)));
        eyeScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
        ScheduleBlink();
    }

    static void Animate(ScaleTransform t, double to, int ms)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
