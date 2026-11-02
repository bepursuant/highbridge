using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbridge.Imaging;

namespace Highbridge.Provenance;

/// <summary>The stamped page and the record that was printed on it.</summary>
public sealed record StampedImage(byte[] Png, int Width, int Height, PageProvenance Provenance);

/// <summary>
/// Adds the provenance band to one PNG page. The canvas is extended at the top or bottom; the
/// scanned pixels are copied unchanged, so nothing on the document is covered. Text is drawn in
/// solid black on white with a bitmap font, which survives thresholding, printing and fax.
/// </summary>
public static class ProvenanceStamper
{
    public const string MetadataKeyword = "highbridge:provenance";

    private const string SegmentGap = "   ";

    /// <summary>Target size of one font dot: about 0.45 mm, so capitals are about 3 mm tall.</summary>
    private const double DotsPerInchPerScale = 56;

    private const int MinCharsPerLine = 24;

    /// <param name="record">
    /// Everything except the hash and band height, which are computed here (pass any placeholder).
    /// </param>
    /// <param name="fallbackDpi">Used when the PNG has no pHYs chunk.</param>
    public static StampedImage Stamp(ReadOnlySpan<byte> png, PageProvenance record, int fallbackDpi)
    {
        var image = PngDecoder.Decode(png);
        var (hash, bilevel) = HashPixels(image, image.Height, rowOffset: 0);
        int dpi = image.Dpi > 0 ? image.Dpi : Math.Max(fallbackDpi, 50);

        var layout = Layout(image.Width, dpi);
        var withHash = record with { OriginalSha256 = hash, BandHeight = 0 };
        var lines = Arrange(BandLines(withHash), layout.CharsPerLine);
        int bandHeight = layout.BandHeight(lines.Count);
        var provenance = withHash with { BandHeight = bandHeight };
        byte[] band = RenderBand(image.Width, bandHeight, lines, layout, provenance.Position);

        int channels = image.Channels;
        var format = bilevel ? PngPixelFormat.Bilevel : channels == 1 ? PngPixelFormat.Gray8 : PngPixelFormat.Rgb24;
        int contentTop = provenance.Position == StampPosition.Top ? bandHeight : 0;
        int height = image.Height + bandHeight;
        var text = Metadata(provenance);

        byte[] output = PngEncoder.Encode(image.Width, height, format, (y, row) =>
        {
            int contentY = y - contentTop;
            if (contentY >= 0 && contentY < image.Height)
            {
                image.ReadRow(contentY, row);
                return;
            }

            int bandY = provenance.Position == StampPosition.Top ? y : y - image.Height;
            var source = band.AsSpan(bandY * image.Width, image.Width);
            if (format == PngPixelFormat.Rgb24)
            {
                for (int x = 0; x < source.Length; x++)
                {
                    row[x * 3] = row[(x * 3) + 1] = row[(x * 3) + 2] = source[x];
                }
            }
            else
            {
                source.CopyTo(row);
            }
        }, dpi, text);

        return new StampedImage(output, image.Width, height, provenance);
    }

    /// <summary>
    /// SHA-256 of <paramref name="rows"/> rows starting at <paramref name="rowOffset"/>, in the
    /// <see cref="PageProvenance.PixelHashMethod"/> form: the ASCII line "highbridge-pixels-v1\n",
    /// width and height (32-bit big-endian), the channel count (1 gray, 3 RGB), then every row as
    /// 8-bit samples. Also reports whether the area is pure black and white.
    /// </summary>
    internal static (string Sha256, bool Bilevel) HashPixels(PngImage image, int rows, int rowOffset)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(PageProvenance.PixelHashMethod + "\n"));
        Span<byte> header = stackalloc byte[9];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], rows);
        header[8] = (byte)image.Channels;
        hash.AppendData(header);

        bool bilevel = image.IsGray;
        var row = new byte[image.Width * image.Channels];
        for (int y = 0; y < rows; y++)
        {
            image.ReadRow(rowOffset + y, row);
            hash.AppendData(row);
            if (bilevel && row.AsSpan().ContainsAnyExcept((byte)0, (byte)255))
            {
                bilevel = false;
            }
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), bilevel);
    }

    /// <summary>One logical row. <see cref="Right"/> is drawn flush with the right margin ("Page N of M").</summary>
    internal sealed record BandLine(IReadOnlyList<string> Segments, string? Right = null);

    /// <summary>A row as drawn: left-aligned text, and optional text flush with the right margin.</summary>
    internal readonly record struct BandTextLine(string Left, string? Right);

    /// <summary>Labels are left-aligned and padded to this width so the colons share a column.</summary>
    private static readonly int LabelWidth = "HIGHBRIDGE ID".Length;

    /// <summary>The band's five lines. Names sit on the left; values start after " : ".</summary>
    internal static IReadOnlyList<BandLine> BandLines(PageProvenance p) =>
    [
        new([Field("HIGHBRIDGE ID", p.ScanId)], $"PAGE {p.Page} OF {p.PageCount}"),
        new([Field("TIMESTAMP", FormatLocal(p.ScannedAt))]),
        new([Field("WORKSTATION", p.Workstation) + "  USER : " + p.User]),
        new([Field("SCANNER", p.Scanner)]),
        new([Field("SHA-256 HASH", HashPrefix(p.OriginalSha256))]),
    ];

    private static string Field(string label, string value) => label.PadRight(LabelWidth) + " : " + value;

    /// <summary>First 32 hex digits of the page hash. The full value stays in the PNG record.</summary>
    private static string HashPrefix(string hash) => hash.Length <= 32 ? hash : hash[..32] + "...";

    public static string FormatLocal(DateTimeOffset local) =>
        local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" + local.ToString("zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// Wraps each logical line and keeps "Page N of M" on the right. It shares the scanner's last
    /// line when both fit; otherwise it gets a line of its own.
    /// </summary>
    internal static List<BandTextLine> Arrange(IReadOnlyList<BandLine> lines, int charsPerLine)
    {
        var drawn = new List<BandTextLine>();
        foreach (var line in lines)
        {
            var left = Wrap([line.Segments.ToArray()], charsPerLine);
            if (line.Right is null)
            {
                drawn.AddRange(left.Select(text => new BandTextLine(text, null)));
                continue;
            }

            string right = ForBand(line.Right);
            int gap = SegmentGap.Length;
            if (left.Count > 0 && left[^1].Length + gap + right.Length <= charsPerLine)
            {
                drawn.AddRange(left.Take(left.Count - 1).Select(text => new BandTextLine(text, null)));
                drawn.Add(new BandTextLine(left[^1], right));
            }
            else
            {
                drawn.AddRange(left.Select(text => new BandTextLine(text, null)));
                drawn.Add(new BandTextLine(string.Empty, right));
            }
        }

        return drawn;
    }

    /// <summary>Upper case ASCII the bitmap font can draw: accents are dropped, anything else becomes '?'.</summary>
    internal static string ForBand(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            char upper = char.ToUpperInvariant(c);
            sb.Append(upper is >= ' ' and <= '~' && BitmapFont.Has(upper) ? upper : '?');
        }

        return sb.ToString();
    }

    /// <summary>Packs segments into lines of at most <paramref name="width"/> characters.</summary>
    internal static List<string> Wrap(IReadOnlyList<string[]> logicalLines, int width)
    {
        var lines = new List<string>();
        foreach (var segments in logicalLines)
        {
            string current = string.Empty;
            foreach (string raw in segments)
            {
                string segment = ForBand(raw);
                string candidate = current.Length == 0 ? segment : current + SegmentGap + segment;
                if (candidate.Length <= width)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                }

                current = segment;
                while (current.Length > width)
                {
                    int cut = current.LastIndexOf(' ', width);
                    if (cut < width / 2)
                    {
                        cut = width;
                    }

                    lines.Add(current[..cut].TrimEnd());
                    current = current[cut..].TrimStart();
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }
        }

        return lines;
    }

    private static BandLayout Layout(int width, int dpi)
    {
        int scale = Math.Max(1, (int)Math.Round(dpi / DotsPerInchPerScale));
        // Keep a quarter inch clear at the sides: printers cannot print right up to the edge.
        int margin = Math.Max(4, dpi / 4);
        if (width - (2 * margin) < MinCharsPerLine * 6)
        {
            margin = Math.Max(2, width / 40);
        }

        int usable = width - (2 * margin);
        scale = Math.Max(1, Math.Min(scale, usable / (MinCharsPerLine * (BitmapFont.GlyphWidth + 1))));
        int charsPerLine = Math.Max(1, (usable + scale) / ((BitmapFont.GlyphWidth + 1) * scale));
        return new BandLayout(scale, margin, Math.Max(4 * scale, dpi / 8), charsPerLine);
    }

    private static byte[] RenderBand(int width, int height, IReadOnlyList<BandTextLine> lines, BandLayout layout, StampPosition position)
    {
        var canvas = new Canvas(width, height);
        (byte, byte, byte) black = (0, 0, 0);
        int rule = layout.Scale;
        int ruleY = position == StampPosition.Top ? height - rule : 0;
        canvas.FillRect(0, ruleY, width, rule, black);

        int y = position == StampPosition.Top ? layout.OuterPadding : rule + layout.InnerPadding;
        foreach (var line in lines)
        {
            if (line.Left.Length > 0)
            {
                canvas.DrawText(line.Left, layout.Margin, y, layout.Scale, black);
            }

            if (line.Right is not null)
            {
                int textWidth = Canvas.MeasureText(line.Right, layout.Scale);
                canvas.DrawText(line.Right, width - layout.Margin - textWidth, y, layout.Scale, black);
            }

            y += layout.LineHeight;
        }

        var gray = new byte[width * height];
        canvas.CopyRedChannel(gray);
        return gray;
    }

    private static List<PngText> Metadata(PageProvenance p)
    {
        string description = string.Join('\n', BandLines(p).Select(line =>
        {
            string left = string.Join(SegmentGap, line.Segments);
            return line.Right is null ? left : left + SegmentGap + line.Right;
        }));
        return
        [
            new("Software", p.Software),
            new("Creation Time", p.ScannedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new("Source", p.Scanner, Utf8: true),
            new("Description", description, Utf8: true),
            new(MetadataKeyword, JsonSerializer.Serialize(p, HighbridgeJson.Options), Utf8: true),
        ];
    }

    private sealed record BandLayout(int Scale, int Margin, int OuterPadding, int CharsPerLine)
    {
        public int InnerPadding => 4 * Scale;

        public int LineHeight => 10 * Scale;

        public int BandHeight(int lines) =>
            Scale + InnerPadding + (lines * LineHeight) - (3 * Scale) + OuterPadding;
    }
}
