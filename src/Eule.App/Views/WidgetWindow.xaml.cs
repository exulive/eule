using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Eule.App.Controls;
using Eule.App.Speech;
using Eule.App.Theme;
using Eule.Core.Cloud;
using Eule.Core.Engine;
using Eule.Core.Localization;
using Serilog;
using Forms = System.Windows.Forms;

namespace Eule.App.Views;

/// <summary>
/// Eule's home: a small tab glued to the right edge of the screen that can only be slid up and down.
/// Normally just the owl (the clock appears in the last 5 minutes before a break). Clicking the owl widens the same
/// frame and offers two ways to take a note: the keyboard, or the microphone (dictation turned into text on this
/// computer). Notes go to the server and the owl nods once it has confirmed them.
/// </summary>
public partial class WidgetWindow : Window
{
    enum Panel { None, Prompt, Keyboard, Listen, Download, Reply }

    const int ClockFromSeconds = 5 * 60;
    static readonly TimeSpan ShowAnswerFor = TimeSpan.FromSeconds(7);
    /// <summary>Opened with a click and left alone: slide back after this, unless the mouse is on it.</summary>
    static readonly TimeSpan IdleExpandedFor = TimeSpan.FromSeconds(4);
    /// <summary>The mouse left the open frame (nothing to read in it): slide back after this.</summary>
    static readonly TimeSpan AfterMouseLeaves = TimeSpan.FromSeconds(2.5);
    static readonly Duration SlideTime = TimeSpan.FromMilliseconds(380);

    readonly AppHost host;
    readonly Shell shell;
    readonly DispatcherTimer collapseTimer = new();
    readonly List<Rectangle> bars = [];
    static readonly Random Rng = new();

    Panel panel = Panel.None;
    bool expanded, busy, collapsing;
    MicRecorder? recorder;
    CancellationTokenSource? work;
    Guid? savedNoteId;

    // vertical drag
    bool pressing, dragging;
    double pressScreenY, pressTop;

    /// <summary>The frame closed back to the owl (the shell may hide a widget it showed only for this).</summary>
    public event Action? Collapsed;

    public WidgetWindow(AppHost host, Shell shell)
    {
        this.host = host;
        this.shell = shell;
        InitializeComponent();
        BuildBars();

        Owl.MouseLeftButtonDown += OnOwlDown;
        Owl.MouseMove += OnOwlMove;
        Owl.MouseLeftButtonUp += OnOwlUp;
        Card.MouseRightButtonUp += (_, e) => { shell.ShowMenu(this); e.Handled = true; };
        Card.MouseEnter += (_, _) => { if (panel is Panel.None or Panel.Reply) collapseTimer.Stop(); };
        Card.MouseLeave += (_, _) =>
        {
            if (expanded && !busy && !collapsing && panel is Panel.None or Panel.Reply)
                Restart(panel == Panel.Reply ? ShowAnswerFor : AfterMouseLeaves);
        };
        collapseTimer.Tick += (_, _) =>
        {
            collapseTimer.Stop();
            // Never pull the frame away from under the mouse; leaving it starts the timer again.
            if (Card.IsMouseOver && panel is Panel.None or Panel.Reply) return;
            if (!busy) Collapse();
        };

        KeyboardButton.Click += (_, _) => OpenKeyboard();
        MicButton.Click += (_, _) => OnMic();
        NotesButton.Click += (_, _) => { Collapse(); shell.OpenNotes(); };
        SendButton.Click += async (_, _) => await SendTypedAsync();
        StopButton.Click += (_, _) => recorder?.Stop();
        UndoButton.Click += async (_, _) => await UndoAsync();
        SignInButton.Click += (_, _) => { Collapse(); shell.OpenLogin(); };
        PrivacyButton.Click += (_, _) => Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        DownloadButton.Click += async (_, _) => await DownloadModelAsync();
        CancelDownloadButton.Click += (_, _) => { work?.Cancel(); Collapse(); };

        AcceptButton.Click += (_, _) => host.Do(e => e.StartBreakNow());
        SnoozeButton.Click += (_, _) => host.Do(e => e.Snooze());
        SkipButton.Click += (_, _) => host.Do(e => e.Skip());
        StartButton.Click += (_, _) => host.Do(e => e.StartWork());

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Collapse(); }
            else if (e.Key == Key.Enter && panel == Panel.Keyboard) { e.Handled = true; await SendTypedAsync(); }
        };
        Deactivated += (_, _) =>
        {
            if (panel == Panel.Keyboard && !busy && string.IsNullOrWhiteSpace(InputBox.Text)) Collapse();
        };

        SourceInitialized += (_, _) => SetActivatable(false);
        Loaded += (_, _) => PlaceInitially();
        SizeChanged += (_, _) => Dock();
    }

    // ------------------------------------------------------------------ engine state

    public void Refresh()
    {
        var e = host.Engine;
        Owl.Accent = ThemeService.AccentFor(e);
        Owl.Progress = e.Progress;
        // While Eule is working (download, transcription, sending) the owl keeps its expression.
        if (!busy) Owl.Mood = panel == Panel.Listen ? OwlMood.Listening : Present.Mood(e);
        Owl.ToolTip = Present.Status(e);

        bool prompt = e.Phase is Phase.BreakDue or Phase.Deferred or Phase.BreakFinished;
        bool clock = prompt || (e.Phase == Phase.Working && e.WorkLeft <= ClockFromSeconds);
        TimeBlock.Visibility = clock && !expanded ? Visibility.Visible : Visibility.Collapsed;
        Caption.Text = Present.Caption(e);
        Time.Text = Present.Time(e);

        // The break question takes the frame only when the user is not in the middle of a note.
        if (prompt && panel == Panel.None && !expanded) Show(Panel.Prompt);
        else if (!prompt && panel == Panel.Prompt) Show(Panel.None);
        if (panel == Panel.Prompt) FillPrompt(e);
    }

    void FillPrompt(BreakEngine e)
    {
        bool due = e.Phase == Phase.BreakDue, finished = e.Phase == Phase.BreakFinished, deferred = e.Phase == Phase.Deferred;
        PromptText.Text = L.T(due ? "widget.prompt.due" : finished ? "widget.prompt.finished" : "widget.prompt.deferred");
        AcceptButton.Visibility = due || deferred ? Visibility.Visible : Visibility.Collapsed;
        SnoozeButton.Visibility = (due || deferred) && e.CanSnooze ? Visibility.Visible : Visibility.Collapsed;
        SnoozeButton.Content = L.F("widget.snooze", e.Settings.SnoozeMinutes);
        SkipButton.Visibility = due && e.CanSkip ? Visibility.Visible : Visibility.Collapsed;
        StartButton.Visibility = finished ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ open / close

    public bool IsExpanded => expanded;

    void Expand()
    {
        if (collapsing) { Reopen(); return; }
        StopSlide();
        double before = Card.ActualWidth;
        // Keep the frame off screen until it has its new width, then slide it in from the edge.
        Slide.X = 4000;
        expanded = true;
        ModeButtons.Visibility = Visibility.Visible;
        if (panel == Panel.Prompt) Show(Panel.None);
        Refresh();
        UpdateLayout();
        SlideTo(from: Math.Max(0, Card.ActualWidth - before), to: 0, ease: EasingMode.EaseOut);
        Restart(IdleExpandedFor);
    }

    /// <summary>Clicked again while sliding away: come back.</summary>
    void Reopen()
    {
        double x = Slide.X;
        StopSlide();
        collapsing = false;
        SlideTo(from: x, to: 0, ease: EasingMode.EaseOut);
        Restart(IdleExpandedFor);
    }

    /// <summary>Slides the open frame back to the edge, then closes it to the owl.</summary>
    public void Collapse()
    {
        if (collapsing) return;
        // Stop listening or sending at once; only the picture takes its time.
        work?.Cancel();
        recorder?.Dispose();
        recorder = null;
        busy = false;
        collapseTimer.Stop();
        if (!expanded || !IsVisible) { CollapseNow(); return; }

        collapsing = true;
        double owlOnly = Owl.ActualWidth + Card.Padding.Left + Card.Padding.Right + Card.BorderThickness.Left;
        SlideTo(from: 0, to: Math.Max(0, Card.ActualWidth - owlOnly), ease: EasingMode.EaseIn, done: () =>
        {
            if (!collapsing) return; // reopened meanwhile
            collapsing = false;
            CollapseNow();
            StopSlide();
        });
    }

    void SlideTo(double from, double to, EasingMode ease, Action? done = null)
    {
        if (Math.Abs(from - to) < 1 || SystemParameters.ClientAreaAnimation == false)
        {
            StopSlide();
            Slide.X = to;
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation(from, to, SlideTime)
        {
            EasingFunction = new CubicEase { EasingMode = ease },
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (done != null) a.Completed += (_, _) => done();
        Slide.BeginAnimation(TranslateTransform.XProperty, a);
    }

    void StopSlide()
    {
        Slide.BeginAnimation(TranslateTransform.XProperty, null);
        Slide.X = 0;
    }

    void CollapseNow()
    {
        collapsing = false;
        expanded = false;
        ModeButtons.Visibility = Visibility.Collapsed;
        KeyboardButton.Tag = MicButton.Tag = null;
        InputBox.Text = "";
        Show(Panel.None);
        SetActivatable(false);
        collapseTimer.Stop();
        Refresh();
        Collapsed?.Invoke();
    }

    void Show(Panel p)
    {
        panel = p;
        PromptPanel.Visibility = p == Panel.Prompt ? Visibility.Visible : Visibility.Collapsed;
        KeyboardPanel.Visibility = p == Panel.Keyboard ? Visibility.Visible : Visibility.Collapsed;
        ListenPanel.Visibility = p == Panel.Listen ? Visibility.Visible : Visibility.Collapsed;
        DownloadPanel.Visibility = p == Panel.Download ? Visibility.Visible : Visibility.Collapsed;
        ReplyPanel.Visibility = p == Panel.Reply ? Visibility.Visible : Visibility.Collapsed;
        Body.Visibility = p == Panel.None ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Opens straight into typing (tray menu "Write a note").</summary>
    public void OpenKeyboard()
    {
        if (!host.Api.IsSignedIn) { shell.OpenLogin(after: () => { if (host.Api.IsSignedIn) OpenKeyboard(); }); return; }
        if (!expanded) Expand();
        collapseTimer.Stop();
        KeyboardButton.Tag = "on";
        MicButton.Tag = null;
        Show(Panel.Keyboard);
        SetActivatable(true);
        Activate();
        Dispatcher.BeginInvoke(() => InputBox.Focus(), DispatcherPriority.Input);
    }

    public void StartDictation() => OnMic();

    void OnMic()
    {
        if (recorder != null) { recorder.Stop(); return; }
        if (busy) return;
        if (!host.Api.IsSignedIn) { shell.OpenLogin(after: () => { if (host.Api.IsSignedIn) OnMic(); }); return; }
        if (!expanded) Expand();
        collapseTimer.Stop();
        MicButton.Tag = "on";
        KeyboardButton.Tag = null;
        SetActivatable(false);

        if (!host.Speech.IsModelReady)
        {
            DownloadText.Text = L.F("asst.modelTitle", SpeechService.ApproxModelMegabytes);
            DownloadBar.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Visible;
            Show(Panel.Download);
            return;
        }
        StartListening();
    }

    // ------------------------------------------------------------------ dictation

    void StartListening()
    {
        Show(Panel.Listen);
        ListenText.Text = L.T("asst.listening");
        StopButton.Visibility = Visibility.Visible;
        Owl.Mood = OwlMood.Listening;
        recorder = new MicRecorder();
        recorder.Level += level => Dispatcher.BeginInvoke(() => ShowLevel(level));
        recorder.Finished += result => Dispatcher.BeginInvoke(async () => await OnRecordedAsync(result));
        recorder.Start();
    }

    async Task OnRecordedAsync(MicResult result)
    {
        recorder = null;
        ShowLevel(0);
        if (panel != Panel.Listen) return; // collapsed meanwhile

        if (result.Problem == MicProblem.NoDevice) { Answer(L.T("asst.noMic"), ok: false); return; }
        if (result.Problem is MicProblem.Blocked or MicProblem.Failed)
        {
            Answer(L.T("asst.micBlocked"), ok: false);
            PrivacyButton.Visibility = Visibility.Visible;
            return;
        }
        if (!result.HeardSpeech) { Answer(L.T("asst.noSpeech"), ok: false); return; }

        busy = true;
        ListenText.Text = L.T("asst.transcribing");
        StopButton.Visibility = Visibility.Collapsed;
        Owl.Mood = OwlMood.Thinking;
        work = new CancellationTokenSource();
        string text;
        try
        {
            text = await host.Speech.TranscribeAsync(result.Samples, work.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Error(ex, "Yazıya çevirme başarısız");
            busy = false;
            Answer(L.T("asst.transcribeFailed"), ok: false);
            return;
        }
        busy = false;
        if (string.IsNullOrWhiteSpace(text)) { Answer(L.T("asst.noSpeech"), ok: false); return; }
        await SubmitAsync(text, "voice");
    }

    async Task DownloadModelAsync()
    {
        busy = true;
        DownloadButton.Visibility = Visibility.Collapsed;
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.Value = 0;
        Owl.Mood = OwlMood.Thinking;
        work = new CancellationTokenSource();
        var progress = new Progress<double>(p =>
        {
            DownloadBar.Value = p;
            DownloadText.Text = L.F("asst.downloading", (int)(p * 100));
        });
        try
        {
            await host.Speech.DownloadModelAsync(progress, work.Token);
            busy = false;
            StartListening();
        }
        catch (OperationCanceledException) { busy = false; }
        catch (Exception ex)
        {
            Log.Warning(ex, "Konuşma modeli indirilemedi");
            busy = false;
            Owl.Shake();
            Owl.Mood = OwlMood.Awake;
            DownloadText.Text = L.T("asst.downloadFailed");
            DownloadBar.Visibility = Visibility.Collapsed;
            DownloadButton.Visibility = Visibility.Visible;
        }
    }

    void BuildBars()
    {
        for (int i = 0; i < 5; i++)
        {
            var r = new Rectangle { Width = 4, Height = 4, RadiusX = 2, RadiusY = 2, Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center };
            r.SetResourceReference(Shape.FillProperty, "Eule.Break");
            bars.Add(r);
            Bars.Children.Add(r);
        }
    }

    void ShowLevel(float level)
    {
        foreach (var r in bars)
            r.Height = 4 + 22 * Math.Min(1, level * (0.55 + Rng.NextDouble() * 0.6));
    }

    // ------------------------------------------------------------------ sending

    async Task SendTypedAsync()
    {
        string text = InputBox.Text.Trim();
        if (busy || text.Length == 0) return;
        await SubmitAsync(text, "text");
    }

    async Task SubmitAsync(string text, string source)
    {
        busy = true;
        Show(Panel.Reply);
        HeardText.Text = L.F("asst.heard", text);
        HeardText.Visibility = source == "voice" ? Visibility.Visible : Visibility.Collapsed;
        ReplyText.Text = L.T("note.sending");
        UndoButton.Visibility = SignInButton.Visibility = PrivacyButton.Visibility = Visibility.Collapsed;
        Owl.Mood = OwlMood.Thinking;
        SetActivatable(false);

        DictationOutcome outcome;
        try
        {
            outcome = await host.Dictations.SubmitAsync(text, source);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Not gönderilemedi");
            outcome = new DictationOutcome(DictationStatus.Queued, Error: ex.Message);
        }
        busy = false;

        switch (outcome.Status)
        {
            case DictationStatus.Saved:
                savedNoteId = outcome.Result!.Note?.Id;
                Answer(outcome.Result.Reply, ok: true);
                UndoButton.Visibility = savedNoteId != null ? Visibility.Visible : Visibility.Collapsed;
                shell.NotesChanged();
                break;
            case DictationStatus.Queued:
                Answer(L.T("note.queued"), ok: false);
                break;
            case DictationStatus.NeedsSignIn:
                Answer(L.T("note.needsSignIn"), ok: false);
                SignInButton.Visibility = Visibility.Visible;
                break;
            default:
                Answer(L.T("note.rejected"), ok: false);
                break;
        }
    }

    void Answer(string text, bool ok)
    {
        Show(Panel.Reply);
        if (!ok) HeardText.Visibility = Visibility.Collapsed;
        ReplyText.Text = text;
        UndoButton.Visibility = Visibility.Collapsed;
        Owl.Mood = OwlMood.Awake;
        if (ok) Owl.Nod(); else Owl.Shake();
        KeyboardButton.Tag = MicButton.Tag = null;
        if (!Card.IsMouseOver) Restart(ShowAnswerFor);
    }

    async Task UndoAsync()
    {
        if (savedNoteId is not Guid id || busy) return;
        busy = true;
        collapseTimer.Stop();
        UndoButton.IsEnabled = false;
        try
        {
            await host.Api.DeleteNoteAsync(id);
            ReplyText.Text = L.T("note.undone");
            HeardText.Visibility = UndoButton.Visibility = Visibility.Collapsed;
            Log.Information("Not geri alındı: {Id}", id);
            shell.NotesChanged();
        }
        catch (ApiException ex)
        {
            Owl.Shake();
            ReplyText.Text = ex.Message;
        }
        UndoButton.IsEnabled = true;
        busy = false;
        Restart(TimeSpan.FromSeconds(2.5));
    }

    void Restart(TimeSpan after)
    {
        collapseTimer.Stop();
        collapseTimer.Interval = after;
        collapseTimer.Start();
    }

    // ------------------------------------------------------------------ right-edge dock, vertical drag

    void PlaceInitially()
    {
        var wa = SystemParameters.WorkArea;
        Top = host.Settings.WidgetTop is double t ? t : wa.Bottom - ActualHeight - 96;
        Dock();
        Refresh();
    }

    /// <summary>Right edge flush with the screen; the top stays where the user put it, within the screen.</summary>
    void Dock()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth;
        Top = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - ActualHeight));
    }

    double DipPerPixel => 1 / VisualTreeHelper.GetDpi(this).DpiScaleY;

    void OnOwlDown(object sender, MouseButtonEventArgs e)
    {
        pressing = true;
        dragging = false;
        pressScreenY = Forms.Cursor.Position.Y;
        pressTop = Top;
        Owl.CaptureMouse();
        e.Handled = true;
    }

    void OnOwlMove(object sender, MouseEventArgs e)
    {
        if (!pressing) return;
        double dy = (Forms.Cursor.Position.Y - pressScreenY) * DipPerPixel;
        if (!dragging && Math.Abs(dy) < 4) return;
        dragging = true;
        Top = pressTop + dy;
        Dock();
    }

    void OnOwlUp(object sender, MouseButtonEventArgs e)
    {
        if (!pressing) return;
        pressing = false;
        Owl.ReleaseMouseCapture();
        if (dragging)
        {
            host.SaveUiState(s => s.WidgetTop = Top);
            return;
        }
        // A click on the owl: open the note choices, or close the frame again.
        if (collapsing) Reopen();
        else if (expanded) Collapse();
        else Expand();
    }

    // ------------------------------------------------------------------ window style

    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint h, int i, nint v);

    /// <summary>
    /// The widget never takes focus from the user's work, except while they type into it.
    /// Always hidden from Alt+Tab.
    /// </summary>
    void SetActivatable(bool on)
    {
        var h = new WindowInteropHelper(this).Handle;
        if (h == 0) return;
        nint style = GetWindowLongPtr(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW;
        style = on ? style & ~(nint)WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE;
        SetWindowLongPtr(h, GWL_EXSTYLE, style);
    }
}
