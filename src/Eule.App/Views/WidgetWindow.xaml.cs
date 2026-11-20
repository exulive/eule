using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Eule.App.Theme;
using Eule.Core.Engine;
using Eule.Core.Localization;

namespace Eule.App.Views;

/// <summary>The small always-on-top companion in the screen corner. Never steals focus; drag to move.</summary>
public partial class WidgetWindow : Window
{
    readonly AppHost host;
    readonly Shell shell;
    Size lastSize;

    public WidgetWindow(AppHost host, Shell shell)
    {
        this.host = host;
        this.shell = shell;
        InitializeComponent();

        PauseButton.Click += (_, _) => host.Do(e => e.TogglePause());
        HideButton.Click += (_, _) => shell.SetWidgetVisible(false);
        AcceptButton.Click += (_, _) => host.Do(e => e.StartBreakNow());
        SnoozeButton.Click += (_, _) => host.Do(e => e.Snooze());
        SkipButton.Click += (_, _) => host.Do(e => e.Skip());
        StartButton.Click += (_, _) => host.Do(e => e.StartWork());

        Card.MouseEnter += (_, _) => FadeTools(1);
        Card.MouseLeave += (_, _) => FadeTools(0);
        Card.MouseLeftButtonDown += OnCardMouseDown;
        Card.MouseRightButtonUp += (_, e) => { shell.ShowMenu(this); e.Handled = true; };

        SourceInitialized += (_, _) => MakeToolWindow();
        Loaded += (_, _) => PlaceInitially();
        SizeChanged += OnSizeChanged;
    }

    public void Refresh()
    {
        var e = host.Engine;
        var accent = ThemeService.AccentFor(e);
        Owl.Accent = accent;
        Owl.Progress = e.Progress;
        Owl.Mood = Present.Mood(e);
        Caption.Text = Present.Caption(e);
        Time.Text = Present.Time(e);

        PauseButton.Content = e.Phase == Phase.Paused ? "" : "";
        PauseButton.ToolTip = L.T(e.Phase == Phase.Paused ? "widget.resume" : "widget.pause");
        PauseButton.Visibility = e.Phase is Phase.Working or Phase.Paused ? Visibility.Visible : Visibility.Collapsed;

        bool due = e.Phase == Phase.BreakDue;
        bool finished = e.Phase == Phase.BreakFinished;
        bool deferred = e.Phase == Phase.Deferred;
        Prompt.Visibility = due || finished || deferred ? Visibility.Visible : Visibility.Collapsed;
        PromptText.Text = due ? L.T("widget.prompt.due")
            : finished ? L.T("widget.prompt.finished")
            : deferred ? L.T("widget.prompt.deferred")
            : "";
        AcceptButton.Visibility = due || deferred ? Visibility.Visible : Visibility.Collapsed;
        SnoozeButton.Visibility = (due || deferred) && e.CanSnooze ? Visibility.Visible : Visibility.Collapsed;
        SnoozeButton.Content = L.F("widget.snooze", e.Settings.SnoozeMinutes);
        SkipButton.Visibility = due && e.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
    }

    void FadeTools(double to) =>
        Tools.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)));

    /// <summary>Drag moves the widget; a click (no movement) talks to the owl: it opens the note bubble.</summary>
    void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        double left = Left, top = Top;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Math.Abs(Left - left) < 3 && Math.Abs(Top - top) < 3)
        {
            shell.OpenNote();
            return;
        }
        host.SaveUiState(s => { s.WidgetLeft = Left; s.WidgetTop = Top; });
    }

    void PlaceInitially()
    {
        var s = host.Settings;
        var wa = SystemParameters.WorkArea;
        double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
        double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;

        if (s.WidgetLeft is double l && s.WidgetTop is double t &&
            l >= vx - 20 && t >= vy - 20 && l + 80 <= vx + vw && t + 60 <= vy + vh)
        {
            Left = l;
            Top = t;
        }
        else
        {
            // Bottom-right corner; the card's own margin leaves room for the shadow.
            Left = wa.Right - ActualWidth - 4;
            Top = wa.Bottom - ActualHeight - 4;
        }
        lastSize = new Size(ActualWidth, ActualHeight);
    }

    /// <summary>When the prompt grows the card, grow towards the screen centre so the widget stays in its corner.</summary>
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (lastSize.Width > 0)
        {
            var wa = SystemParameters.WorkArea;
            double dw = e.NewSize.Width - lastSize.Width, dh = e.NewSize.Height - lastSize.Height;
            if (Left + lastSize.Width / 2 > wa.Left + wa.Width / 2) Left -= dw;
            if (Top + lastSize.Height / 2 > wa.Top + wa.Height / 2) Top -= dh;
        }
        lastSize = e.NewSize;
    }

    // Hide from Alt+Tab and never take focus from the app the user is working in.
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint h, int i, nint v);

    void MakeToolWindow()
    {
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLongPtr(h, GWL_EXSTYLE, GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }
}
