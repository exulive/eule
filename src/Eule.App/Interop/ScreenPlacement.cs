using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Eule.App.Interop;

/// <summary>Puts a borderless window over one whole monitor, whatever that monitor's DPI is.</summary>
static class ScreenPlacement
{
    /// <summary>Call before Show(). The window is moved in physical pixels, then maximized on that monitor.</summary>
    public static void CoverScreen(Window w, Forms.Screen screen)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.WindowState = WindowState.Normal;
        w.SourceInitialized += (_, _) =>
        {
            var b = screen.Bounds;
            var h = new WindowInteropHelper(w).Handle;
            SetWindowPos(h, 0, b.X, b.Y, b.Width, b.Height, SWP_NOZORDER | SWP_NOACTIVATE);
        };
        w.Loaded += (_, _) => w.WindowState = WindowState.Maximized;
    }

    public static Forms.Screen Primary => Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];

    public static IEnumerable<Forms.Screen> Others => Forms.Screen.AllScreens.Where(s => !s.Primary);

    const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(nint hWnd, nint after, int x, int y, int cx, int cy, uint flags);
}
