using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Eule.Core.Cloud;
using Eule.Core.Localization;
using Serilog;

namespace Eule.App.Views;

/// <summary>
/// The same three views as the web page:
/// <list type="bullet">
/// <item>Dictation history: everything said or typed, newest first, grouped by day, with what Eule filed from it.
/// Dictations still waiting in the local outbox (offline) are listed too.</item>
/// <item>Agenda: dated items in time order, overdue ones first.</item>
/// <item>To-dos: open items, then (optionally) the finished ones.</item>
/// </list>
/// Deleting asks once inline ("Delete?"); agenda and to-do items can be ticked off.
/// </summary>
public partial class NotesWindow : Wpf.Ui.Controls.FluentWindow
{
    const string DeleteGlyph = "";

    readonly AppHost host;
    readonly Shell shell;
    CancellationTokenSource? loading;

    List<DictationInfo> dictations = [];
    List<NoteInfo> notes = [];
    string? error;

    public NotesWindow(AppHost host, Shell shell)
    {
        this.host = host;
        this.shell = shell;
        InitializeComponent();
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        SignInButton.Click += (_, _) => shell.OpenLogin(after: () => _ = ReloadAsync());
        WebButton.Click += (_, _) =>
        {
            if (host.Api.Server is Uri web) Process.Start(new ProcessStartInfo(web.ToString()) { UseShellExecute = true });
        };
        foreach (var tab in new[] { HistoryTab, AgendaTab, TodoTab })
            tab.Checked += (_, _) => { if (IsLoaded) { Render(); Scroller.ScrollToTop(); } };
        ShowDone.Checked += (_, _) => Render();
        ShowDone.Unchecked += (_, _) => Render();
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => loading?.Cancel();
    }

    enum View { History, Agenda, Todo }

    View Current => AgendaTab.IsChecked == true ? View.Agenda : TodoTab.IsChecked == true ? View.Todo : View.History;

    public async Task ReloadAsync()
    {
        loading?.Cancel();
        var cts = loading = new CancellationTokenSource();
        SignInButton.Visibility = Visibility.Collapsed;

        if (!host.Api.IsSignedIn)
        {
            ShowSignIn();
            return;
        }
        if (List.Children.Count == 0) Status(L.T("notes.loading"));
        RefreshButton.IsEnabled = false;

        try
        {
            var d = host.Api.GetDictationsAsync(cts.Token);
            var n = host.Api.GetNotesAsync(ct: cts.Token);
            await Task.WhenAll(d, n);
            dictations = d.Result;
            notes = n.Result.Where(x => !x.Deleted).ToList();
            error = null;
        }
        catch (OperationCanceledException) { return; }
        catch (ApiException ex)
        {
            Log.Warning(ex, "Notlar alınamadı");
            if (ex.Kind == ApiError.SignedOut)
            {
                RefreshButton.IsEnabled = true;
                ShowSignIn();
                return;
            }
            error = L.T("notes.offline");
        }
        finally
        {
            if (loading == cts) RefreshButton.IsEnabled = true;
        }
        if (cts.IsCancellationRequested) return;
        Render();
    }

    void ShowSignIn()
    {
        dictations = [];
        notes = [];
        List.Children.Clear();
        Status(L.T("notes.signIn"));
        SignInButton.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ views

    void Render()
    {
        if (!host.Api.IsSignedIn) return;
        var view = Current;
        ShowDone.Visibility = view == View.History ? Visibility.Collapsed : Visibility.Visible;
        List.Children.Clear();
        StatusText.Visibility = Visibility.Collapsed;

        bool any = view switch
        {
            View.Agenda => RenderAgenda(),
            View.Todo => RenderTodos(),
            _ => RenderHistory(),
        };
        if (error != null) Status(error);
        else if (!any) Status(view switch
        {
            View.Agenda => L.T("notes.agendaEmpty"),
            View.Todo => notes.Any(n => n.Kind == "todo") ? L.T("notes.allDone") : L.T("notes.todoEmpty"),
            _ => L.T("notes.empty"),
        });
    }

    bool RenderHistory()
    {
        var rows = new List<(DateTime At, Border Card)>();
        foreach (var p in host.Database.OutboxPending())
            rows.Add((Local(p.CreatedAt), HistoryCard(p.Text, Local(p.CreatedAt), [],
                $"{SourceText(p.Source)}  ·  {L.T("notes.pending")}", pending: true,
                () => { host.Database.OutboxRemove(p.ClientId); return Task.CompletedTask; })));
        foreach (var d in dictations)
            rows.Add((Local(d.ReceivedAt), HistoryCard(d.Text, Local(d.ReceivedAt), d.Notes.Select(Item).ToList(),
                SourceText(d.Source), pending: false, async () =>
                {
                    await host.Api.DeleteDictationAsync(d.Id);
                    dictations.Remove(d);
                    notes.RemoveAll(n => n.DictationId == d.Id || d.Notes.Any(x => x.Id == n.Id));
                })));

        var today = DateTime.Now.Date;
        DateTime? day = null;
        foreach (var (at, card) in rows.OrderByDescending(r => r.At))
        {
            if (at.Date != day)
            {
                day = at.Date;
                Section(DayLabel(at.Date, today));
            }
            List.Children.Add(card);
        }
        return rows.Count > 0;
    }

    bool RenderAgenda()
    {
        bool showDone = ShowDone.IsChecked == true;
        var now = DateTime.Now;
        var list = notes.Where(n => n.Kind == "event" && n.DueAt != null && (showDone || !n.Done))
            .OrderBy(n => n.DueAt).ToList();
        bool Late(NoteInfo n)
        {
            if (n.Done) return false;
            var at = InZone(n.DueAt!.Value, n.TimeZone);
            return n.HasTime ? at < now : at.Date < now.Date;
        }

        var overdue = list.Where(Late).ToList();
        if (overdue.Count > 0)
        {
            Section(L.F("notes.overdue", overdue.Count), B("Eule.Warn"));
            foreach (var n in overdue) List.Children.Add(NoteCard(n, timeOnly: false));
        }
        DateTime? day = null;
        foreach (var n in list.Where(n => !Late(n)))
        {
            var at = InZone(n.DueAt!.Value, n.TimeZone);
            if (at.Date != day)
            {
                day = at.Date;
                Section(DayLabel(at.Date, now.Date));
            }
            List.Children.Add(NoteCard(n, timeOnly: true));
        }
        return list.Count > 0;
    }

    bool RenderTodos()
    {
        var all = notes.Where(n => n.Kind == "todo").OrderByDescending(n => n.CreatedAt).ToList();
        var open = all.Where(n => !n.Done).ToList();
        var done = all.Where(n => n.Done).ToList();
        bool showDone = ShowDone.IsChecked == true;
        if (open.Count > 0)
        {
            Section(L.F("notes.todoOpen", open.Count));
            foreach (var n in open) List.Children.Add(NoteCard(n, timeOnly: false));
        }
        if (showDone && done.Count > 0)
        {
            Section(L.F("notes.todoDone", done.Count));
            foreach (var n in done) List.Children.Add(NoteCard(n, timeOnly: false));
        }
        return open.Count > 0 || (showDone && done.Count > 0);
    }

    // ------------------------------------------------------------------ cards

    /// <summary>One dictation: what was said, what Eule filed from it (agenda / to-do / note), and how to delete it.</summary>
    Border HistoryCard(string said, DateTime at, IReadOnlyList<string> items, string meta, bool pending, Func<Task> delete)
    {
        var time = new TextBlock
        {
            Text = at.ToString("HH:mm", L.Culture),
            FontSize = 13, Width = 46, Margin = new Thickness(0, 1, 8, 0),
            Foreground = B("Eule.Muted"),
        };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = $"“{said}”", FontSize = 13, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap,
            Foreground = B("Eule.Muted"),
        });
        foreach (string item in items)
            text.Children.Add(new TextBlock
            {
                Text = item, Style = (Style)FindResource("Eule.RowTitle"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        text.Children.Add(new TextBlock { Text = meta, Style = (Style)FindResource("Eule.RowDesc") });

        var card = Card(time, text, delete);
        if (pending) card.Opacity = 0.75;
        return card;
    }

    /// <summary>An agenda or to-do item: tick box, title, when / details / what was said, delete.</summary>
    Border NoteCard(NoteInfo n, bool timeOnly)
    {
        var tick = new CheckBox
        {
            IsChecked = n.Done, ToolTip = L.T("notes.markDone"),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -3, 0, 0), MinWidth = 0,
        };
        tick.Click += async (_, _) => await SetDoneAsync(n, tick.IsChecked == true);

        var title = new TextBlock
        {
            Text = n.Title, Style = (Style)FindResource("Eule.RowTitle"), TextWrapping = TextWrapping.Wrap,
        };
        if (n.Done)
        {
            title.TextDecorations = TextDecorations.Strikethrough;
            title.Foreground = B("Eule.Muted");
        }
        var meta = new List<string>();
        if (n.DueAt is DateTime due)
        {
            var at = InZone(due, n.TimeZone);
            meta.Add(timeOnly
                ? (n.HasTime ? at.ToString("HH:mm", L.Culture) : L.T("notes.allDay"))
                : at.ToString(n.HasTime ? "d MMMM ddd, HH:mm" : "d MMMM ddd", L.Culture));
        }
        if (!string.IsNullOrWhiteSpace(n.Body)) meta.Add(n.Body);
        meta.Add($"“{n.Text}”");

        var text = new StackPanel();
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", meta), Style = (Style)FindResource("Eule.RowDesc"), TextWrapping = TextWrapping.Wrap,
        });

        return Card(tick, text, async () =>
        {
            await host.Api.DeleteNoteAsync(n.Id);
            notes.RemoveAll(x => x.Id == n.Id);
            foreach (var d in dictations) d.Notes.RemoveAll(x => x.Id == n.Id);
        });
    }

    async Task SetDoneAsync(NoteInfo n, bool done)
    {
        try
        {
            await host.Api.SetDoneAsync(n.Id, done);
            Replace(n with { Done = done });
            Log.Information("Not {Durum} (Notlarım)", done ? "tamamlandı" : "yeniden açıldı");
        }
        catch (ApiException ex)
        {
            Log.Warning(ex, "Not güncellenemedi");
            Render();
            Status(L.F("notes.updateFailed", ex.Message));
            return;
        }
        Render();
    }

    void Replace(NoteInfo updated)
    {
        int i = notes.FindIndex(x => x.Id == updated.Id);
        if (i >= 0) notes[i] = updated;
        foreach (var d in dictations)
        {
            int j = d.Notes.FindIndex(x => x.Id == updated.Id);
            if (j >= 0) d.Notes[j] = updated;
        }
    }

    /// <summary>Lead (time or tick box) on the left, text in the middle, delete on the right.</summary>
    Border Card(FrameworkElement lead, FrameworkElement text, Func<Task> delete)
    {
        var button = new Button
        {
            Content = DeleteGlyph,
            FontFamily = (FontFamily)FindResource("Eule.IconFont"),
            ToolTip = L.T("notes.delete"),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(8, -2, -6, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = B("Eule.Muted"),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(lead, Dock.Left);
        DockPanel.SetDock(button, Dock.Right);
        dock.Children.Add(lead);
        dock.Children.Add(button);
        dock.Children.Add(text);

        var card = new Border { Style = (Style)FindResource("Eule.Row"), Child = dock, Padding = new Thickness(14, 10, 12, 10) };

        // First click asks, second click deletes; moving away cancels the question.
        bool asking = false;
        button.MouseLeave += (_, _) =>
        {
            if (!asking) return;
            asking = false;
            button.Content = DeleteGlyph;
            button.FontFamily = (FontFamily)FindResource("Eule.IconFont");
            button.Foreground = B("Eule.Muted");
        };
        button.Click += async (_, _) =>
        {
            if (!asking)
            {
                asking = true;
                button.Content = L.T("notes.confirmDelete");
                button.FontFamily = (FontFamily)FindResource("Eule.Font");
                button.Foreground = B("Eule.Warn");
                return;
            }
            button.IsEnabled = false;
            try
            {
                await delete();
                Log.Information("Silindi (Notlarım)");
                Render();
            }
            catch (ApiException ex)
            {
                button.IsEnabled = true;
                Status(L.F("notes.deleteFailed", ex.Message));
            }
        };
        return card;
    }

    void Section(string text, Brush? color = null)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("Eule.SectionTitle"),
            Margin = new Thickness(2, 22, 0, 8),
        };
        if (color != null) block.Foreground = color;
        List.Children.Add(block);
    }

    // ------------------------------------------------------------------ text

    /// <summary>"Ajanda · Ayşe'yi ara · 23 Kasım Pzt, 15:00", "Yapılacak · Süt al · Tamamlandı".</summary>
    static string Item(NoteInfo n)
    {
        var parts = new List<string>
        {
            L.T(n.Kind switch { "event" => "notes.kind.event", "todo" => "notes.kind.todo", _ => "notes.kind.note" }),
            n.Title,
        };
        if (n.DueAt is DateTime due)
            parts.Add(InZone(due, n.TimeZone).ToString(n.HasTime ? "d MMMM ddd, HH:mm" : "d MMMM ddd", L.Culture));
        if (n.Done) parts.Add(L.T("notes.done"));
        return string.Join("  ·  ", parts);
    }

    static string SourceText(string source) => L.T(source == "text" ? "notes.typed" : "notes.voice");

    static string DayLabel(DateTime day, DateTime today) =>
        day == today ? L.T("notes.today")
        : day == today.AddDays(-1) ? L.T("notes.yesterday")
        : day == today.AddDays(1) ? L.T("notes.tomorrow")
        : day.ToString(day.Year == today.Year ? "d MMMM dddd" : "d MMMM yyyy, dddd", L.Culture);

    static DateTime Local(DateTime utc) =>
        utc == default ? DateTime.Now : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();

    /// <summary>A reminder is shown in the zone it was understood in (IANA id from the server), else local time.</summary>
    static DateTime InZone(DateTime utc, string zone)
    {
        utc = DateTime.SpecifyKind(utc.ToUniversalTime(), DateTimeKind.Utc);
        if (!string.IsNullOrEmpty(zone) && TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz))
            return TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        return utc.ToLocalTime();
    }

    void Status(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    static Brush B(string key) => (Brush)Application.Current.Resources[key];
}
