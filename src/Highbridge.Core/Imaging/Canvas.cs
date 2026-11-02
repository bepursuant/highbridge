namespace Highbridge.Imaging;

/// <summary>Tiny raster surface used to draw mock pages. Always RGB internally.</summary>
internal sealed class Canvas
{
    private readonly byte[] _rgb;

    public Canvas(int width, int height)
    {
        Width = width;
        Height = height;
        _rgb = new byte[width * height * 3];
        Array.Fill(_rgb, (byte)255);
    }

    public int Width { get; }

    public int Height { get; }

    public void FillRect(int x, int y, int w, int h, (byte R, byte G, byte B) color)
    {
        int x0 = Math.Clamp(x, 0, Width), x1 = Math.Clamp(x + w, 0, Width);
        int y0 = Math.Clamp(y, 0, Height), y1 = Math.Clamp(y + h, 0, Height);
        for (int py = y0; py < y1; py++)
        {
            int row = py * Width * 3;
            for (int px = x0; px < x1; px++)
            {
                int i = row + (px * 3);
                _rgb[i] = color.R;
                _rgb[i + 1] = color.G;
                _rgb[i + 2] = color.B;
            }
        }
    }

    public void StrokeRect(int x, int y, int w, int h, int thickness, (byte R, byte G, byte B) color)
    {
        FillRect(x, y, w, thickness, color);
        FillRect(x, y + h - thickness, w, thickness, color);
        FillRect(x, y, thickness, h, color);
        FillRect(x + w - thickness, y, thickness, h, color);
    }

    /// <summary>
    /// Draws a grid of modules (a barcode) of <paramref name="moduleWidth"/> x <paramref name="moduleHeight"/>
    /// pixels, centered on (<paramref name="centerX"/>, <paramref name="centerY"/>) and turned
    /// <paramref name="angleDegrees"/> clockwise.
    /// </summary>
    public void DrawModules(int columns, int rows, Func<int, int, bool> isDark, double centerX, double centerY, double moduleWidth, double moduleHeight, double angleDegrees, (byte R, byte G, byte B) color)
    {
        double w = columns * moduleWidth, h = rows * moduleHeight;
        double radians = angleDegrees * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double reach = Math.Sqrt((w * w) + (h * h)) / 2;
        int x0 = Math.Max(0, (int)(centerX - reach)), x1 = Math.Min(Width, (int)Math.Ceiling(centerX + reach));
        int y0 = Math.Max(0, (int)(centerY - reach)), y1 = Math.Min(Height, (int)Math.Ceiling(centerY + reach));
        for (int py = y0; py < y1; py++)
        {
            double dy = py + 0.5 - centerY;
            for (int px = x0; px < x1; px++)
            {
                double dx = px + 0.5 - centerX;
                double u = (dx * cos) + (dy * sin) + (w / 2);
                double v = (-dx * sin) + (dy * cos) + (h / 2);
                if (u < 0 || v < 0 || u >= w || v >= h || !isDark((int)(u / moduleWidth), (int)(v / moduleHeight)))
                {
                    continue;
                }

                int i = ((py * Width) + px) * 3;
                _rgb[i] = color.R;
                _rgb[i + 1] = color.G;
                _rgb[i + 2] = color.B;
            }
        }
    }

    /// <returns>The rendered width in pixels.</returns>
    public int DrawText(string text, int x, int y, int scale, (byte R, byte G, byte B) color)
    {
        int cursor = x;
        foreach (char c in text.ToUpperInvariant())
        {
            var glyph = BitmapFont.Get(c);
            for (int row = 0; row < BitmapFont.GlyphHeight; row++)
            {
                for (int col = 0; col < BitmapFont.GlyphWidth; col++)
                {
                    if (glyph[(row * BitmapFont.GlyphWidth) + col])
                    {
                        FillRect(cursor + (col * scale), y + (row * scale), scale, scale, color);
                    }
                }
            }

            cursor += (BitmapFont.GlyphWidth + 1) * scale;
        }

        return cursor - x;
    }

    /// <summary>Copies the red channel, one byte per pixel (the gray value for black-and-white drawings).</summary>
    public void CopyRedChannel(Span<byte> destination)
    {
        for (int i = 0; i < Width * Height; i++)
        {
            destination[i] = _rgb[i * 3];
        }
    }

    public static int MeasureText(string text, int scale) => text.Length * (BitmapFont.GlyphWidth + 1) * scale;

    public byte[] ToPng(bool grayscale, bool threshold, int dpi = 0)
    {
        if (!grayscale)
        {
            return PngEncoder.Encode(Width, Height, PngPixelFormat.Rgb24, _rgb, dpi);
        }

        var gray = new byte[Width * Height];
        for (int i = 0; i < gray.Length; i++)
        {
            int luma = ((_rgb[i * 3] * 299) + (_rgb[(i * 3) + 1] * 587) + (_rgb[(i * 3) + 2] * 114)) / 1000;
            gray[i] = threshold ? (byte)(luma < 160 ? 0 : 255) : (byte)luma;
        }

        return PngEncoder.Encode(Width, Height, PngPixelFormat.Gray8, gray, dpi);
    }
}

/// <summary>Upper-case 5x7 font covering printable ASCII (lower case is drawn as upper case).</summary>
internal static class BitmapFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;

    private static readonly Dictionary<char, bool[]> Glyphs = Build();

    public static bool[] Get(char c) => Glyphs.TryGetValue(c, out var g) ? g : Glyphs['?'];

    public static bool Has(char c) => Glyphs.ContainsKey(c);

    private static Dictionary<char, bool[]> Build()
    {
        var source = new Dictionary<char, string>
        {
            ['A'] = ".###.|#...#|#...#|#####|#...#|#...#|#...#",
            ['B'] = "####.|#...#|#...#|####.|#...#|#...#|####.",
            ['C'] = ".###.|#...#|#....|#....|#....|#...#|.###.",
            ['D'] = "####.|#...#|#...#|#...#|#...#|#...#|####.",
            ['E'] = "#####|#....|#....|####.|#....|#....|#####",
            ['F'] = "#####|#....|#....|####.|#....|#....|#....",
            ['G'] = ".###.|#...#|#....|#.###|#...#|#...#|.####",
            ['H'] = "#...#|#...#|#...#|#####|#...#|#...#|#...#",
            ['I'] = ".###.|..#..|..#..|..#..|..#..|..#..|.###.",
            ['J'] = "..###|...#.|...#.|...#.|...#.|#..#.|.##..",
            ['K'] = "#...#|#..#.|#.#..|##...|#.#..|#..#.|#...#",
            ['L'] = "#....|#....|#....|#....|#....|#....|#####",
            ['M'] = "#...#|##.##|#.#.#|#.#.#|#...#|#...#|#...#",
            ['N'] = "#...#|#...#|##..#|#.#.#|#..##|#...#|#...#",
            ['O'] = ".###.|#...#|#...#|#...#|#...#|#...#|.###.",
            ['P'] = "####.|#...#|#...#|####.|#....|#....|#....",
            ['Q'] = ".###.|#...#|#...#|#...#|#.#.#|#..#.|.##.#",
            ['R'] = "####.|#...#|#...#|####.|#.#..|#..#.|#...#",
            ['S'] = ".####|#....|#....|.###.|....#|....#|####.",
            ['T'] = "#####|..#..|..#..|..#..|..#..|..#..|..#..",
            ['U'] = "#...#|#...#|#...#|#...#|#...#|#...#|.###.",
            ['V'] = "#...#|#...#|#...#|#...#|#...#|.#.#.|..#..",
            ['W'] = "#...#|#...#|#...#|#.#.#|#.#.#|#.#.#|.#.#.",
            ['X'] = "#...#|#...#|.#.#.|..#..|.#.#.|#...#|#...#",
            ['Y'] = "#...#|#...#|.#.#.|..#..|..#..|..#..|..#..",
            ['Z'] = "#####|....#|...#.|..#..|.#...|#....|#####",
            ['0'] = ".###.|#...#|#..##|#.#.#|##..#|#...#|.###.",
            ['1'] = "..#..|.##..|..#..|..#..|..#..|..#..|.###.",
            ['2'] = ".###.|#...#|....#|...#.|..#..|.#...|#####",
            ['3'] = "#####|...#.|..#..|...#.|....#|#...#|.###.",
            ['4'] = "...#.|..##.|.#.#.|#..#.|#####|...#.|...#.",
            ['5'] = "#####|#....|####.|....#|....#|#...#|.###.",
            ['6'] = "..##.|.#...|#....|####.|#...#|#...#|.###.",
            ['7'] = "#####|....#|...#.|..#..|.#...|.#...|.#...",
            ['8'] = ".###.|#...#|#...#|.###.|#...#|#...#|.###.",
            ['9'] = ".###.|#...#|#...#|.####|....#|...#.|.##..",
            ['/'] = "....#|...#.|...#.|..#..|.#...|.#...|#....",
            ['-'] = ".....|.....|.....|#####|.....|.....|.....",
            [':'] = ".....|..##.|..##.|.....|..##.|..##.|.....",
            ['.'] = ".....|.....|.....|.....|.....|.##..|.##..",
            ['?'] = ".###.|#...#|....#|...#.|..#..|.....|..#..",
            [' '] = ".....|.....|.....|.....|.....|.....|.....",
            ['\\'] = "#....|.#...|.#...|..#..|...#.|...#.|....#",
            ['@'] = ".###.|#...#|#.###|#.#.#|#.###|#....|.###.",
            ['_'] = ".....|.....|.....|.....|.....|.....|#####",
            ['+'] = ".....|..#..|..#..|#####|..#..|..#..|.....",
            ['('] = "...#.|..#..|.#...|.#...|.#...|..#..|...#.",
            [')'] = ".#...|..#..|...#.|...#.|...#.|..#..|.#...",
            [','] = ".....|.....|.....|.....|.##..|..#..|.#...",
            ['\''] = "..#..|..#..|.#...|.....|.....|.....|.....",
            ['"'] = ".#.#.|.#.#.|.#.#.|.....|.....|.....|.....",
            ['&'] = ".##..|#..#.|#.#..|.#...|#.#.#|#..#.|.##.#",
            ['#'] = ".#.#.|.#.#.|#####|.#.#.|#####|.#.#.|.#.#.",
            ['='] = ".....|.....|#####|.....|#####|.....|.....",
            ['!'] = "..#..|..#..|..#..|..#..|..#..|.....|..#..",
            ['$'] = "..#..|.####|#.#..|.###.|..#.#|####.|..#..",
            ['%'] = "##...|##..#|...#.|..#..|.#...|#..##|...##",
            ['*'] = ".....|..#..|#.#.#|.###.|#.#.#|..#..|.....",
            [';'] = ".....|.##..|.##..|.....|.##..|..#..|.#...",
            ['['] = ".###.|.#...|.#...|.#...|.#...|.#...|.###.",
            [']'] = ".###.|...#.|...#.|...#.|...#.|...#.|.###.",
            ['<'] = "...#.|..#..|.#...|#....|.#...|..#..|...#.",
            ['>'] = ".#...|..#..|...#.|....#|...#.|..#..|.#...",
            ['{'] = "...##|..#..|..#..|.#...|..#..|..#..|...##",
            ['}'] = "##...|..#..|..#..|...#.|..#..|..#..|##...",
            ['|'] = "..#..|..#..|..#..|..#..|..#..|..#..|..#..",
            ['~'] = ".....|.....|.#...|#.#.#|...#.|.....|.....",
            ['^'] = "..#..|.#.#.|#...#|.....|.....|.....|.....",
            ['`'] = ".#...|..#..|...#.|.....|.....|.....|.....",
        };

        var glyphs = new Dictionary<char, bool[]>();
        foreach (var (c, pattern) in source)
        {
            glyphs[c] = pattern.Replace("|", string.Empty, StringComparison.Ordinal).Select(ch => ch == '#').ToArray();
        }

        return glyphs;
    }
}
