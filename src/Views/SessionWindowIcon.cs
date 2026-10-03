using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Material.Icons;

namespace TermThing.Views;

/// <summary>
/// Renders a session's Material icon (in its colour) as a window icon, so a floated
/// session can be told apart in the taskbar / window switcher.
/// </summary>
internal static class SessionWindowIcon
{
    private const int Size = 64;          // px; the OS scales down as needed
    private const double GlyphInset = 8;  // margin around the glyph inside the tile
    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#1E1E1E"));

    public static WindowIcon? TryCreate(MaterialIconKind kind, IBrush glyphBrush)
    {
        try
        {
            var geometry = StreamGeometry.Parse(MaterialIconDataProvider.GetData(kind));

            using var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
            using (var ctx = bitmap.CreateDrawingContext())
            {
                // Dark tile keeps light glyph colours readable on light taskbars too.
                ctx.DrawRectangle(TileBrush, null, new RoundedRect(new Rect(0, 0, Size, Size), 12));

                // Material glyphs are drawn on a 24×24 grid.
                double scale = (Size - 2 * GlyphInset) / 24.0;
                using (ctx.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(GlyphInset, GlyphInset)))
                    ctx.DrawGeometry(glyphBrush, null, geometry);
            }

            var png = new MemoryStream();
            bitmap.Save(png, PngBitmapEncoderOptions.Default);
            png.Position = 0;
            return new WindowIcon(png);
        }
        catch (Exception ex)
        {
            Diagnostics.Log.Warn("ui", $"Could not render window icon for {kind}", ex);
            return null;
        }
    }
}
