using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Eule.Core.Insights;
using Eule.Core.Localization;
using Eule.Core.Storage;
using Serilog;

namespace Eule.App.Views;

/// <summary>
/// Statistics: three headline numbers, sitting per day for two weeks (one series, one hue),
/// a weekday × hour heat map (one hue, light to dark) and a few plain-language insights.
/// Every bar and cell has a tooltip; the CSV export is the full table view.
/// </summary>
public partial class StatsWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly AppHost host;
    Report? report;

    public StatsWindow(AppHost host)
    {
        this.host = host;
        InitializeComponent();
        ExportButton.Click += (_, _) => Export();
        Activated += (_, _) => Render();
        host.SettingsChanged += Render; // theme or goal changed
        Closed += (_, _) => host.SettingsChanged -= Render;
    }

    void Render()
    {
        try
        {
            host.Stats.Flush();
            report = Report.Build(host.Database, host.Stats.Today, host.Settings.DailyBreakGoal);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "İstatistik okunamadı");
            return;
        }
        RenderTiles(report);
        RenderBars(report);
        RenderHeatmap(report);
        RenderInsights(report);
    }

    static Brush B(string key) => (Brush)Application.Current.Resources[key];
    static Color C(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    static TextBlock Text(string text, double size, string brush, FontWeight? weight = null) => new()
    {
        Text = text, FontSize = size, Foreground = B(brush), FontWeight = weight ?? FontWeights.Normal,
    };

    // ------------------------------------------------------------------ tiles

    void RenderTiles(Report r)
    {
        Tiles.Children.Clear();
        var today = r.Recent[^1];
        string weekDelta = r.LastWeekSit > 0 ? L.F("stats.lastWeek", Words.Duration(r.LastWeekSit)) : L.T("stats.lastWeekNone");
        Tiles.Children.Add(Tile(Words.Duration(r.ThisWeekSit), L.T("stats.weekSit"), weekDelta, first: true));
        Tiles.Children.Add(Tile(r.ThisWeekBreaks.ToString(), L.T("stats.weekBreaks"), L.F("stats.todayGoal", today.AllBreaks, r.Goal)));
        Tiles.Children.Add(Tile(r.Streak.Days == 0 ? "—" : L.F("unit.days", r.Streak.Days), L.T("stats.streak"),
            r.Streak.TodayMet ? L.T("stats.goalMet") : L.F("stats.goal", r.Goal)));
    }

    static Border Tile(string value, string label, string sub, bool first = false)
    {
        var tb = Text(value, 26, "Eule.Ink");
        tb.FontWeight = FontWeights.Light;
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        var border = new Border
        {
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(first ? 0 : 6, 0, 0, 0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    Text(label, 12, "Eule.Muted"),
                    tb,
                    new TextBlock { Text = sub, FontSize = 11.5, Foreground = B("Eule.Muted"), TextTrimming = TextTrimming.CharacterEllipsis },
                },
            },
        };
        border.SetResourceReference(Border.BackgroundProperty, "Eule.Raised");
        border.SetResourceReference(Border.BorderBrushProperty, "Eule.Line");
        return border;
    }

    // ------------------------------------------------------------------ bars

    void RenderBars(Report r)
    {
        BarChart.Children.Clear();
        BarChart.ColumnDefinitions.Clear();
        BarChart.RowDefinitions.Clear();
        BarChart.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        BarChart.ColumnDefinitions.Add(new ColumnDefinition());
        BarChart.RowDefinitions.Add(new RowDefinition());
        BarChart.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        double maxHours = r.Recent.Max(d => d.SitSeconds) / 3600.0;
        int step = maxHours <= 4 ? 1 : 2;
        int top = Math.Max(step * 2, (int)Math.Ceiling(maxHours / step) * step);

        // Recessive grid lines with hour labels.
        var grid = new Grid();
        var labels = new Grid();
        for (int h = 0; h <= top; h += step)
        {
            double y = 1 - (double)h / top;
            var line = new Rectangle { Height = 1, Fill = B("Eule.Line"), VerticalAlignment = VerticalAlignment.Top };
            grid.Children.Add(line);
            var label = Text(h == 0 ? "" : L.F("unit.hour", h), 10.5, "Eule.Muted"); // the baseline needs no label
            label.VerticalAlignment = VerticalAlignment.Top;
            labels.Children.Add(label);
            void Place(object? s, SizeChangedEventArgs? e)
            {
                line.Margin = new Thickness(0, y * grid.ActualHeight, 0, 0);
                label.Margin = new Thickness(0, Math.Max(0, y * grid.ActualHeight - 7), 0, 0);
            }
            grid.SizeChanged += Place;
        }
        Grid.SetColumn(grid, 1);
        BarChart.Children.Add(grid);
        BarChart.Children.Add(labels);

        // One column per day: bar, day label, goal dot. The whole column is the hover target.
        var columns = new UniformGrid { Columns = r.Recent.Count, Rows = 1 };
        var footer = new UniformGrid { Columns = r.Recent.Count, Rows = 1, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var d in r.Recent)
        {
            bool isToday = d.Date == r.Today;
            var result = Streak.Judge(d, r.Goal);
            double frac = d.SitSeconds / 3600.0 / top;

            var bar = new Border
            {
                Width = 16,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(4, 4, 0, 0),
                Background = B("Eule.Sit"),
                Opacity = isToday ? 1 : 0.85,
            };
            var cell = new Grid { Background = Brushes.Transparent, Children = { bar } };
            cell.SizeChanged += (_, e) => bar.Height = d.SitSeconds == 0 ? 0 : Math.Max(3, frac * e.NewSize.Height);
            cell.ToolTip = L.F("stats.barTip", Words.Date(d.Date), Words.Duration(d.SitSeconds), d.AllBreaks) +
                           (d.LongestStretch > 0 ? L.F("stats.barLongest", Words.Duration(d.LongestStretch)) : "") +
                           result switch { DayResult.Met => L.T("stats.barMet"), DayResult.Missed => L.T("stats.barMissed"), _ => "" };
            cell.MouseEnter += (_, _) => bar.Opacity = 0.65;
            cell.MouseLeave += (_, _) => bar.Opacity = isToday ? 1 : 0.85;
            columns.Children.Add(cell);

            var dayLabel = Text(isToday ? L.T("stats.today") : Words.DayShort[Words.WeekdayIndex(d.Date)], 10.5,
                isToday ? "Eule.Ink" : "Eule.Muted", isToday ? FontWeights.SemiBold : null);
            dayLabel.HorizontalAlignment = HorizontalAlignment.Center;
            var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            if (result == DayResult.Met) dot.Fill = B("Eule.Ink");
            else if (result == DayResult.Missed && !isToday) { dot.Stroke = B("Eule.Muted"); dot.StrokeThickness = 1.2; }
            else dot.Visibility = Visibility.Hidden;
            footer.Children.Add(new StackPanel { Children = { dayLabel, dot } });
        }
        Grid.SetColumn(columns, 1);
        BarChart.Children.Add(columns);
        Grid.SetColumn(footer, 1);
        Grid.SetRow(footer, 1);
        BarChart.Children.Add(footer);
    }

    // ------------------------------------------------------------------ heat map

    void RenderHeatmap(Report r)
    {
        Heatmap.Children.Clear();
        Heatmap.ColumnDefinitions.Clear();
        Heatmap.RowDefinitions.Clear();

        var (from, to) = r.ActiveHours;
        double max = Math.Max(r.HeatmapMax, 1);
        Color low = C("Eule.Track"), high = C("Eule.Sit");

        Heatmap.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        for (int h = from; h <= to; h++) Heatmap.ColumnDefinitions.Add(new ColumnDefinition());
        Heatmap.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int d = 0; d < 7; d++) Heatmap.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });

        // Hour labels every three hours, so they never collide.
        for (int h = from; h <= to; h++)
        {
            if ((h - from) % 3 != 0) continue;
            var l = Text($"{h:00}", 10.5, "Eule.Muted");
            l.Margin = new Thickness(1, 0, 0, 4);
            Grid.SetColumn(l, h - from + 1);
            Grid.SetColumnSpan(l, 3);
            Heatmap.Children.Add(l);
        }

        for (int d = 0; d < 7; d++)
        {
            var dayLabel = Text(Words.DayShort[d], 11, "Eule.Muted");
            dayLabel.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(dayLabel, d + 1);
            Heatmap.Children.Add(dayLabel);

            for (int h = from; h <= to; h++)
            {
                double v = r.Heatmap[d, h];
                var cell = new Border
                {
                    Margin = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Background = new SolidColorBrush(Lerp(low, high, v / max)),
                    ToolTip = L.F("stats.cellTip", Words.DayName[d], h, (h + 1) % 24, Math.Round(v)),
                };
                Grid.SetRow(cell, d + 1);
                Grid.SetColumn(cell, h - from + 1);
                Heatmap.Children.Add(cell);
            }
        }

        HeatLegend.Children.Clear();
        HeatLegend.Children.Add(new TextBlock { Text = L.T("stats.less"), FontSize = 11, Foreground = B("Eule.Muted"), Margin = new Thickness(0, 0, 6, 0) });
        foreach (double t in new[] { 0, 0.25, 0.5, 0.75, 1.0 })
            HeatLegend.Children.Add(new Border
            {
                Width = 14, Height = 12, Margin = new Thickness(1, 0, 1, 0), CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Lerp(low, high, t)),
            });
        HeatLegend.Children.Add(new TextBlock
        {
            Text = L.F("stats.more", Math.Round(max)), FontSize = 11, Foreground = B("Eule.Muted"), Margin = new Thickness(6, 0, 0, 0),
        });
    }

    /// <summary>Single-hue sequential scale from the empty-track colour to the sitting accent.</summary>
    static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        if (t > 0) t = 0.12 + 0.88 * t; // any sitting at all is visibly different from none
        byte L(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
        return Color.FromRgb(L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    // ------------------------------------------------------------------ insights

    void RenderInsights(Report r)
    {
        InsightList.Children.Clear();
        var items = r.Insights.Count > 0
            ? r.Insights
            : [L.T("stats.insightsEmpty")];
        foreach (string s in items)
        {
            var row = new Border { Style = (Style)FindResource("Eule.Row"), Padding = new Thickness(16, 12, 16, 12) };
            var dot = new Ellipse { Width = 6, Height = 6, Fill = B("Eule.Sit"), Margin = new Thickness(0, 7, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            var text = Text(s, 13.5, "Eule.Ink");
            text.TextWrapping = TextWrapping.Wrap;
            row.Child = new DockPanel { Children = { dot, text } };
            DockPanel.SetDock(dot, Dock.Left);
            InsightList.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ export

    void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = L.F("stats.csvName", DateTime.Today),
            Filter = L.T("stats.csvFilter"),
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            host.Stats.Flush();
            CsvExport.Write(dlg.FileName, host.Database);
            ExportText.Text = L.F("stats.exported", System.IO.Path.GetFileName(dlg.FileName));
            Log.Information("İstatistik dışa aktarıldı: {Path}", dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Dışa aktarılamadı");
            ExportText.Text = L.T("stats.exportFailed");
        }
    }
}
