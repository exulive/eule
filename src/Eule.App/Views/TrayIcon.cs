using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Eule.Core.Engine;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Eule.App.Views;

/// <summary>
/// Notification-area icon: the same ring as the widget, with two owl eyes in the middle.
/// Uses the WinForms NotifyIcon (reliable on .NET 8); the menu itself is a WPF menu opened by <see cref="Shell"/>.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon icon = new();
    Icon? current;
    string lastKey = "";

    public event Action? LeftClick;
    public event Action? DoubleClick;
    public event Action? RightClick;

    public TrayIcon()
    {
        icon.Text = "Eule";
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) LeftClick?.Invoke();
            else if (e.Button == Forms.MouseButtons.Right) RightClick?.Invoke();
        };
        icon.MouseDoubleClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) DoubleClick?.Invoke(); };
        icon.Visible = true;
    }

    public void Update(BreakEngine e, string status)
    {
        string tip = "Eule · " + status;
        icon.Text = tip.Length > 63 ? tip[..63] : tip;

        string accent = Theme.ThemeService.AccentKey(e);
        int step = (int)Math.Round(e.Progress * 24);
        bool closed = e.Phase is Phase.Break or Phase.Away;
        string key = $"{accent}|{step}|{closed}|{TaskbarIsLight()}";
        if (key == lastKey) return;
        lastKey = key;

        var old = current;
        current = Draw(AccentColor(accent), step / 24f, closed, TaskbarIsLight());
        icon.Icon = current;
        old?.Dispose();
    }

    public void Notify(string title, string text) => icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);

    static Color AccentColor(string key) => key switch
    {
        "Eule.Warn" => ColorTranslator.FromHtml("#E0AD63"),
        "Eule.Break" => ColorTranslator.FromHtml("#76ABD8"),
        "Eule.Idle" => ColorTranslator.FromHtml("#9A9488"),
        _ => ColorTranslator.FromHtml("#86B592"),
    };

    static bool TaskbarIsLight()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return k?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }

    static Icon Draw(Color accent, float progress, bool eyesClosed, bool lightTaskbar)
    {
        const int size = 32;
        Color ink = lightTaskbar ? ColorTranslator.FromHtml("#2A2824") : ColorTranslator.FromHtml("#ECE6DA");
        Color track = lightTaskbar ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(70, 255, 255, 255);

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var r = new RectangleF(3, 3, 26, 26);
            using (var p = new Pen(track, 4)) g.DrawEllipse(p, r);
            if (progress > 0)
                using (var p = new Pen(accent, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawArc(p, r, -90, Math.Max(8, 360 * progress));

            using var b = new SolidBrush(ink);
            if (eyesClosed)
            {
                using var p = new Pen(ink, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawArc(p, 9.5f, 12.5f, 6, 5, 20, 140);
                g.DrawArc(p, 16.5f, 12.5f, 6, 5, 20, 140);
            }
            else
            {
                g.FillEllipse(b, 9.5f, 13, 5.5f, 5.5f);
                g.FillEllipse(b, 17f, 13, 5.5f, 5.5f);
            }
            using var beak = new SolidBrush(accent);
            g.FillPolygon(beak, [new PointF(16, 19), new PointF(14.4f, 21.2f), new PointF(16, 23.6f), new PointF(17.6f, 21.2f)]);
        }

        nint h = bmp.GetHicon();
        var result = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return result;
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(nint h);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        current?.Dispose();
    }
}
