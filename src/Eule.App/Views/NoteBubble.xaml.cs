using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Eule.App.Controls;
using Eule.App.Theme;
using Eule.Core.Cloud;
using Eule.Core.Localization;
using Serilog;

namespace Eule.App.Views;

/// <summary>
/// The owl's speech bubble: type a note (voice comes next), the owl thinks while the server reads it,
/// then nods and says what it understood, with a few seconds to undo. If there is no connection the note is
/// kept and sent later; nothing is ever shown as saved before the server has confirmed it.
/// </summary>
public partial class NoteBubble : Window
{
    static readonly TimeSpan ShowAnswerFor = TimeSpan.FromSeconds(7);

    readonly AppHost host;
    readonly DispatcherTimer closeTimer = new();
    Guid? savedNoteId;
    bool busy;

    public event Action? SignInRequested;

    public NoteBubble(AppHost host)
    {
        this.host = host;
        InitializeComponent();
        Owl.Accent = ThemeService.Brush("Eule.Sit");

        SendButton.Click += async (_, _) => await SubmitAsync();
        UndoButton.Click += async (_, _) => await UndoAsync();
        SignInButton.Click += (_, _) => { SignInRequested?.Invoke(); Close(); };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); FadeOut(); };
        Card.MouseEnter += (_, _) => closeTimer.Stop();
        Card.MouseLeave += (_, _) => { if (ReplyPanel.IsVisible && !busy) Restart(closeTimer, ShowAnswerFor); };

        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; Close(); }
            else if (e.Key == Key.Enter && InputPanel.IsVisible) { e.Handled = true; await SubmitAsync(); }
        };
        // Clicking elsewhere before typing anything just puts the bubble away.
        Deactivated += (_, _) =>
        {
            if (InputPanel.IsVisible && !busy && string.IsNullOrWhiteSpace(InputBox.Text)) Close();
        };
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            Activate();
            InputBox.Focus();
        };
    }

    /// <summary>Places the bubble next to the widget (or in the corner if the widget is hidden).</summary>
    public void PlaceNear(Rect? anchor)
    {
        var wa = SystemParameters.WorkArea;
        UpdateLayout();
        double h = ActualHeight > 0 ? ActualHeight : 140;
        if (anchor is Rect a)
        {
            Left = Math.Clamp(a.Right - Width, wa.Left, wa.Right - Width);
            Top = a.Top - h + 18 > wa.Top ? a.Top - h + 18 : a.Bottom - 18;
        }
        else
        {
            Left = wa.Right - Width - 4;
            Top = wa.Bottom - h - 4;
        }
    }

    async Task SubmitAsync()
    {
        string text = InputBox.Text.Trim();
        if (busy || text.Length == 0) return;

        busy = true;
        InputPanel.Visibility = Visibility.Collapsed;
        ReplyPanel.Visibility = Visibility.Visible;
        ReplyText.Text = L.T("note.sending");
        Owl.Mood = OwlMood.Thinking;

        DictationOutcome outcome;
        try
        {
            outcome = await host.Dictations.SubmitAsync(text, "text");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Not gönderilemedi");
            outcome = new DictationOutcome(DictationStatus.Queued, Error: ex.Message);
        }
        busy = false;
        Owl.Mood = OwlMood.Awake;

        switch (outcome.Status)
        {
            case DictationStatus.Saved:
                Owl.Nod();
                ReplyText.Text = outcome.Result!.Reply;
                savedNoteId = outcome.Result.Note?.Id;
                UndoButton.Visibility = savedNoteId != null ? Visibility.Visible : Visibility.Collapsed;
                break;
            case DictationStatus.Queued:
                Owl.Shake();
                ReplyText.Text = L.T("note.queued");
                break;
            case DictationStatus.NeedsSignIn:
                Owl.Shake();
                ReplyText.Text = L.T("note.needsSignIn");
                SignInButton.Visibility = Visibility.Visible;
                break;
            default:
                Owl.Shake();
                ReplyText.Text = L.T("note.rejected");
                break;
        }
        if (!Card.IsMouseOver) Restart(closeTimer, ShowAnswerFor);
    }

    async Task UndoAsync()
    {
        if (savedNoteId is not Guid id || busy) return;
        busy = true;
        closeTimer.Stop();
        UndoButton.IsEnabled = false;
        try
        {
            await host.Api.DeleteNoteAsync(id);
            ReplyText.Text = L.T("note.undone");
            UndoButton.Visibility = Visibility.Collapsed;
            Log.Information("Not geri alındı: {Id}", id);
        }
        catch (ApiException ex)
        {
            Owl.Shake();
            ReplyText.Text = ex.Message;
            UndoButton.IsEnabled = true;
        }
        busy = false;
        Restart(closeTimer, TimeSpan.FromSeconds(2.5));
    }

    void FadeOut()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    static void Restart(DispatcherTimer t, TimeSpan after)
    {
        t.Stop();
        t.Interval = after;
        t.Start();
    }
}
