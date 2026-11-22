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
/// Every note the user dictated or typed, newest first, grouped by day with the time it was said.
/// Notes still waiting in the local outbox (offline) are listed too. Deleting asks once inline ("Delete?").
/// </summary>
public partial class NotesWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly AppHost host;
    readonly Shell shell;
    CancellationTokenSource? loading;

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
        Loaded += async (_, _) => await ReloadAsync();
        Closed += (_, _) => loading?.Cancel();
    }

    public async Task ReloadAsync()
    {
        loading?.Cancel();
        var cts = loading = new CancellationTokenSource();
        SignInButton.Visibility = Visibility.Collapsed;

        if (!host.Api.IsSignedIn)
        {
            List.Children.Clear();
            Status(L.T("notes.signIn"));
            SignInButton.Visibility = Visibility.Visible;
            return;
        }
        if (List.Children.Count == 0) Status(L.T("notes.loading"));
        RefreshButton.IsEnabled = false;

        List<DictationInfo>? dictations = null;
        string? error = null;
        try
        {
            dictations = await host.Api.GetDictationsAsync(cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (ApiException ex)
        {
            Log.Warning(ex, "Notlar alınamadı");
            error = ex.Kind == ApiError.SignedOut ? null : L.T("notes.offline");
            if (ex.Kind == ApiError.SignedOut)
            {
                RefreshButton.IsEnabled = true;
                List.Children.Clear();
                Status(L.T("notes.signIn"));
                SignInButton.Visibility = Visibility.Visible;
                return;
            }
        }
        finally
        {
            if (loading == cts) RefreshButton.IsEnabled = true;
        }
        if (cts.IsCancellationRequested) return;

        var pending = host.Database.OutboxPending();
        Render(dictations ?? [], pending, error);
    }

    // ------------------------------------------------------------------ list

    /// <summary>One dictation: what was said, what Eule filed from it (agenda / to-do / note), and how to delete it.</summary>
    sealed record Row(DateTime At, string Said, IReadOnlyList<string> Items, string Meta, Func<Task>? Delete, bool Pending);

    void Render(List<DictationInfo> dictations, List<Core.Storage.OutboxItem> pending, string? error)
    {
        var rows = new List<Row>();
        foreach (var p in pending)
            rows.Add(new Row(Local(p.CreatedAt), p.Text, [],
                $"{SourceText(p.Source)}  ·  {L.T("notes.pending")}",
                () => { host.Database.OutboxRemove(p.ClientId); return Task.CompletedTask; }, Pending: true));
        foreach (var d in dictations)
            rows.Add(new Row(Local(d.ReceivedAt), d.Text, d.Notes.Select(Item).ToList(), SourceText(d.Source),
                () => host.Api.DeleteDictationAsync(d.Id), Pending: false));

        List.Children.Clear();
        if (rows.Count == 0)
        {
            Status(error ?? L.T("notes.empty"));
            return;
        }
        if (error != null) Status(error); else StatusText.Visibility = Visibility.Collapsed;

        var today = DateTime.Now.Date;
        DateTime? day = null;
        foreach (var row in rows.OrderByDescending(r => r.At))
        {
            if (row.At.Date != day)
            {
                day = row.At.Date;
                List.Children.Add(new TextBlock
                {
                    Text = DayLabel(row.At.Date, today),
                    Style = (Style)FindResource("Eule.SectionTitle"),
                    Margin = new Thickness(2, 22, 0, 8),
                });
            }
            List.Children.Add(Card(row));
        }
    }

    Border Card(Row row)
    {
        var time = new TextBlock
        {
            Text = row.At.ToString("HH:mm", L.Culture),
            FontSize = 13, Width = 46, Margin = new Thickness(0, 1, 8, 0),
            Foreground = B("Eule.Muted"),
        };
        Typography.SetNumeralAlignment(time, FontNumeralAlignment.Tabular);

        var said = new TextBlock
        {
            Text = $"“{row.Said}”", FontSize = 13, FontStyle = FontStyles.Italic, TextWrapping = TextWrapping.Wrap,
            Foreground = B("Eule.Muted"),
        };
        var meta = new TextBlock { Text = row.Meta, Style = (Style)FindResource("Eule.RowDesc") };
        var text = new StackPanel();
        text.Children.Add(said);
        foreach (string item in row.Items)
            text.Children.Add(new TextBlock
            {
                Text = item, Style = (Style)FindResource("Eule.RowTitle"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        text.Children.Add(meta);

        var delete = new Button
        {
            Content = "",
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
        DockPanel.SetDock(time, Dock.Left);
        DockPanel.SetDock(delete, Dock.Right);
        dock.Children.Add(time);
        dock.Children.Add(delete);
        dock.Children.Add(text);

        var card = new Border { Style = (Style)FindResource("Eule.Row"), Child = dock, Padding = new Thickness(14, 10, 12, 10) };
        if (row.Pending) card.Opacity = 0.75;

        // First click asks, second click deletes; moving away cancels the question.
        bool asking = false;
        delete.MouseLeave += (_, _) =>
        {
            if (!asking) return;
            asking = false;
            delete.Content = "";
            delete.FontFamily = (FontFamily)FindResource("Eule.IconFont");
            delete.Foreground = B("Eule.Muted");
        };
        delete.Click += async (_, _) =>
        {
            if (row.Delete == null) return;
            if (!asking)
            {
                asking = true;
                delete.Content = L.T("notes.confirmDelete");
                delete.FontFamily = (FontFamily)FindResource("Eule.Font");
                delete.Foreground = B("Eule.Warn");
                return;
            }
            delete.IsEnabled = false;
            try
            {
                await row.Delete();
                Log.Information("Not silindi (Notlarım)");
                int i = List.Children.IndexOf(card);
                List.Children.Remove(card);
                // Drop the day heading if this was its last note.
                if (i > 0 && List.Children[i - 1] is TextBlock && (i >= List.Children.Count || List.Children[i] is TextBlock))
                    List.Children.RemoveAt(i - 1);
                if (List.Children.Count == 0) Status(L.T("notes.empty"));
            }
            catch (ApiException ex)
            {
                delete.IsEnabled = true;
                Status(L.F("notes.deleteFailed", ex.Message));
            }
        };
        return card;
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
