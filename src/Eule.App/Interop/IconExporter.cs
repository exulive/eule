using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Eule.App.Controls;
using Eule.App.Theme;

namespace Eule.App.Interop;

/// <summary>
/// Renders the app icon from the same <see cref="OwlView"/> the UI uses (developer flag --export-icon &lt;path&gt;):
/// a warm paper tile, the owl inside a three-quarter ring. Writes a multi-size .ico with PNG frames.
/// </summary>
static class IconExporter
{
    static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    public static void Export(string icoPath)
    {
        var frames = Sizes.Select(s => (Size: s, Png: RenderPng(s))).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath))!);
        using var fs = File.Create(icoPath);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);          // reserved
        w.Write((short)1);          // type: icon
        w.Write((short)frames.Count);
        int offset = 6 + 16 * frames.Count;
        foreach (var (size, png) in frames)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);       // palette
            w.Write((byte)0);       // reserved
            w.Write((short)1);      // planes
            w.Write((short)32);     // bits per pixel
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in frames) w.Write(png);

        // A large PNG next to it, handy for the README and a store listing.
        File.WriteAllBytes(Path.ChangeExtension(icoPath, ".png"), RenderPng(512));
    }

    static byte[] RenderPng(int size)
    {
        bool small = size <= 24;
        var owl = new OwlView
        {
            Width = size * (small ? 0.96 : 0.84),
            Height = size * (small ? 0.96 : 0.84),
            Progress = 0.75,
            Accent = ThemeService.Brush("Eule.Sit"),
            ShowRing = !small, // at 16–24 px the ring is noise; the owl alone reads better
        };
        var tile = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.22),
            Background = small ? Brushes.Transparent : ThemeService.Brush("Eule.Surface"),
            Child = owl,
        };
        tile.Measure(new Size(size, size));
        tile.Arrange(new Rect(0, 0, size, size));
        tile.UpdateLayout();

        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(tile);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }
}
