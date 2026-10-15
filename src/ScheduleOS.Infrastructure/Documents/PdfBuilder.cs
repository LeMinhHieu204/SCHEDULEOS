using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace ScheduleOS.Infrastructure.Documents;

/// <summary>Minimal flowing-layout helper on top of PDFsharp (MIT licensed).</summary>
internal sealed class PdfBuilder : IDisposable
{
    private static readonly object FontInit = new();
    private static bool _fontsReady;

    public static readonly XColor Navy = XColor.FromArgb(0x10, 0x25, 0x32);
    public static readonly XColor Teal = XColor.FromArgb(0x00, 0x8C, 0x95);
    public static readonly XColor Muted = XColor.FromArgb(0x67, 0x79, 0x8B);
    public static readonly XColor Border = XColor.FromArgb(0xE2, 0xEB, 0xEF);
    public static readonly XColor Light = XColor.FromArgb(0xF5, 0xF9, 0xFA);

    private readonly PdfDocument _doc = new();
    private XGraphics _gfx = null!;
    private PdfPage _page = null!;

    public const double Margin = 48;
    public double Y { get; set; }
    public double Width => _page.Width.Point - 2 * Margin;
    public double Left => Margin;
    public double Right => _page.Width.Point - Margin;

    public PdfBuilder(string title)
    {
        EnsureFonts();
        _doc.Info.Title = title;
        _doc.Info.Creator = "ScheduleOS";
        NewPage();
    }

    public static XFont Font(double size, bool bold = false) => new("Segoe UI", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

    public void NewPage()
    {
        _gfx?.Dispose();
        _page = _doc.AddPage();
        _page.Size = PdfSharp.PageSize.A4;
        _gfx = XGraphics.FromPdfPage(_page);
        Y = Margin;
    }

    public void EnsureSpace(double height)
    {
        if (Y + height > _page.Height.Point - Margin) NewPage();
    }

    public void Text(string text, XFont font, XColor color, double x, double width, XStringFormat? format = null)
    {
        _gfx.DrawString(text ?? "", font, new XSolidBrush(color), new XRect(x, Y, width, font.Size * 1.5), format ?? XStringFormats.TopLeft);
    }

    public void Line(string text, double size = 10, bool bold = false, XColor? color = null, double spacing = 1.45)
    {
        var font = Font(size, bold);
        foreach (var wrapped in Wrap(text ?? "", font, Width))
        {
            EnsureSpace(size * spacing);
            Text(wrapped, font, color ?? Navy, Left, Width);
            Y += size * spacing;
        }
    }

    public void Rule(double gapBefore = 6, double gapAfter = 8)
    {
        Y += gapBefore;
        _gfx.DrawLine(new XPen(Border, 1), Left, Y, Right, Y);
        Y += gapAfter;
    }

    public void Fill(double x, double y, double w, double h, XColor color) =>
        _gfx.DrawRectangle(new XSolidBrush(color), x, y, w, h);

    /// <summary>Draws a table with proportional column widths; right-aligns columns flagged in <paramref name="rightAligned"/>.</summary>
    public void Table(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows, double[] weights, bool[] rightAligned, double fontSize = 9)
    {
        var total = weights.Sum();
        var widths = weights.Select(w => Width * w / total).ToArray();
        var rowHeight = fontSize * 2.0;
        var headerFont = Font(fontSize, true);
        var font = Font(fontSize);

        void Header()
        {
            EnsureSpace(rowHeight * 2);
            Fill(Left, Y, Width, rowHeight, Light);
            DrawRow(headers, headerFont, Muted);
        }

        void DrawRow(IReadOnlyList<string> cells, XFont f, XColor color)
        {
            var x = Left;
            for (var i = 0; i < widths.Length; i++)
            {
                var cell = i < cells.Count ? cells[i] ?? "" : "";
                cell = Fit(cell, f, widths[i] - 8);
                var rect = new XRect(x + 4, Y + (rowHeight - fontSize * 1.25) / 2, widths[i] - 8, fontSize * 1.4);
                _gfx.DrawString(cell, f, new XSolidBrush(color), rect, rightAligned[i] ? XStringFormats.TopRight : XStringFormats.TopLeft);
                x += widths[i];
            }

            Y += rowHeight;
            _gfx.DrawLine(new XPen(Border, 0.6), Left, Y, Right, Y);
        }

        Header();
        foreach (var row in rows)
        {
            if (Y + rowHeight > _page.Height.Point - Margin)
            {
                NewPage();
                Header();
            }

            DrawRow(row, font, Navy);
        }

        Y += 6;
    }

    public void Save(string path)
    {
        _gfx.Dispose();
        _doc.Save(path);
    }

    public void Dispose()
    {
        _gfx?.Dispose();
        _doc.Dispose();
    }

    private string Fit(string text, XFont font, double width)
    {
        if (_gfx.MeasureString(text, font).Width <= width) return text;
        while (text.Length > 1 && _gfx.MeasureString(text + "…", font).Width > width) text = text[..^1];
        return text + "…";
    }

    private IEnumerable<string> Wrap(string text, XFont font, double width)
    {
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var words = paragraph.Split(' ');
            var line = "";
            foreach (var w in words)
            {
                var candidate = line.Length == 0 ? w : line + " " + w;
                if (_gfx.MeasureString(candidate, font).Width > width && line.Length > 0)
                {
                    yield return line;
                    line = w;
                }
                else line = candidate;
            }

            yield return line;
        }
    }

    private static void EnsureFonts()
    {
        lock (FontInit)
        {
            if (_fontsReady) return;
            if (GlobalFontSettings.FontResolver is null) GlobalFontSettings.FontResolver = new WindowsFontResolver();
            _fontsReady = true;
        }
    }
}

/// <summary>Resolves the UI font family from the Windows font directory (Segoe UI, falling back to Arial).</summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string FontDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly (string Face, string[] Files)[] Faces =
    [
        ("ScheduleOS#Regular", ["segoeui.ttf", "arial.ttf"]),
        ("ScheduleOS#Bold", ["segoeuib.ttf", "arialbd.ttf"]),
    ];

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? "ScheduleOS#Bold" : "ScheduleOS#Regular");

    public byte[]? GetFont(string faceName)
    {
        var files = Faces.FirstOrDefault(f => f.Face == faceName).Files ?? Faces[0].Files;
        foreach (var file in files)
        {
            var path = Path.Combine(FontDir, file);
            if (File.Exists(path)) return File.ReadAllBytes(path);
        }

        throw new InvalidOperationException("No usable TrueType font (Segoe UI or Arial) was found in " + FontDir);
    }
}
