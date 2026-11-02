using Highbridge.Imaging;
using ZXing;
using ZXing.Common;

namespace Highbridge.Barcodes;

/// <summary>A barcode printed on a <see cref="MockBarcodeSheet"/> page.</summary>
/// <param name="Angle">Degrees clockwise from upright.</param>
public sealed record MockBarcode(BarcodeFormat Format, string Text, int Angle);

/// <summary>
/// Test pages for barcode detection: every default format, each turned 0, 90, 180 or 270 degrees
/// (the turn shifts from page to page, so a 3-sheet feeder covers every format at several angles),
/// plus a QR code and a Code 128 that are skewed by a few degrees. Rendered at real scan resolution.
/// </summary>
public static class MockBarcodeSheet
{
    /// <summary>One-dimensional module (narrow bar) width: 0.38 mm, a typical label.</summary>
    private const double BarModuleInches = 0.015;

    /// <summary>2D module size: 0.5 mm.</summary>
    private const double CellModuleInches = 0.02;

    private static readonly (BarcodeFormat Format, string Text)[] Codes =
    [
        (BarcodeFormat.QrCode, "https://highbridge.example/doc/{0}"),
        (BarcodeFormat.DataMatrix, "PT-DM-{0}"),
        (BarcodeFormat.Pdf417, "HIGHBRIDGE PDF417 {0}"),
        (BarcodeFormat.Code128, "PT-C128-{0}"),
        (BarcodeFormat.Code39, "PT-C39-{0}"),
        (BarcodeFormat.Ean13, "400638133393"),
        (BarcodeFormat.Ean8, "9638507"),
        (BarcodeFormat.UpcA, "03600029145"),
        (BarcodeFormat.UpcE, "0123456"),
        (BarcodeFormat.Itf, "1234567890123"),
    ];

    /// <summary>
    /// QR codes read at any angle. A 1D code read along pixel rows only tolerates skew while one row
    /// still crosses every bar (about 10 degrees for this Code 128), so its skew stays smaller.
    /// </summary>
    private static readonly (int Qr, int Linear)[] Skews = [(8, 352), (352, 8), (20, 5)];

    /// <summary>What page <paramref name="pageIndex"/> contains, with the texts a reader should return.</summary>
    public static IReadOnlyList<MockBarcode> Contents(int pageIndex)
    {
        string tag = $"P{pageIndex + 1}";
        var codes = new List<MockBarcode>();
        for (int i = 0; i < Codes.Length; i++)
        {
            var (format, template) = Codes[i];
            int angle = BarcodeDetector.Rotations[(i + pageIndex) % 4];
            codes.Add(new MockBarcode(format, Decoded(format, string.Format(System.Globalization.CultureInfo.InvariantCulture, template, $"{tag}-R{angle}")), angle));
        }

        var (qr, linear) = Skews[pageIndex % Skews.Length];
        codes.Add(new MockBarcode(BarcodeFormat.QrCode, $"SKEW-QR-{tag}", qr));
        codes.Add(new MockBarcode(BarcodeFormat.Code128, $"SKEW-C128-{tag}", linear));
        return codes;
    }

    /// <summary>Page <paramref name="pageIndex"/> as a grayscale PNG, e.g. to print and scan for real.</summary>
    public static byte[] RenderPng(int pageIndex, int pageCount, int dpi) => Render(pageIndex, pageCount, dpi).ToPng(grayscale: true, threshold: false);

    /// <summary>Draws page <paramref name="pageIndex"/> at <paramref name="dpi"/>.</summary>
    internal static Canvas Render(int pageIndex, int pageCount, int dpi)
    {
        int width = (int)Math.Round(8.27 * dpi);
        int height = (int)Math.Round(11.69 * dpi);
        var canvas = new Canvas(width, height);
        (byte, byte, byte) ink = (20, 20, 20);
        int unit = Math.Max(1, dpi / 50);
        int margin = dpi / 2;
        canvas.DrawText($"HIGHBRIDGE BARCODE TEST SHEET  PAGE {pageIndex + 1} OF {pageCount}", margin, margin / 2, unit * 2, ink);

        var codes = Contents(pageIndex);
        const int columns = 3;
        int rows = (codes.Count + columns - 1) / columns;
        double cellWidth = (width - (2.0 * margin)) / columns;
        double cellHeight = (height - (1.5 * margin)) / rows;
        for (int i = 0; i < codes.Count; i++)
        {
            var code = codes[i];
            double cx = margin + ((i % columns) + 0.5) * cellWidth;
            double cy = margin + ((i / columns) + 0.5) * cellHeight;
            DrawCode(canvas, code.Format, code.Text, cx, cy, code.Angle, dpi, ink);
            string label = $"{BarcodeFormatSet.Name(code.Format).ToUpperInvariant()} {code.Angle}";
            canvas.DrawText(label, (int)(cx - (cellWidth / 2) + unit * 4), (int)(cy + (cellHeight / 2) - (unit * 12)), unit, (100, 100, 100));
        }

        return canvas;
    }

    /// <summary>Draws one barcode centered on (<paramref name="cx"/>, <paramref name="cy"/>), turned <paramref name="angle"/> degrees clockwise.</summary>
    internal static void DrawCode(Canvas canvas, BarcodeFormat format, string text, double cx, double cy, double angle, int dpi, (byte R, byte G, byte B) ink)
    {
        var matrix = Encode(format, text);
        bool linear = matrix.Height == 1;
        double module = Math.Max(linear ? 2 : 3, Math.Round((linear ? BarModuleInches : CellModuleInches) * dpi));
        double moduleHeight = linear ? Math.Round(0.5 * dpi) : module;
        canvas.DrawModules(matrix.Width, matrix.Height, (x, y) => matrix[x, linear ? 0 : y], cx, cy, module, moduleHeight, angle, ink);
    }

    /// <summary>Readers return EAN, UPC and ITF numbers with their check digit, so the sheet prints the full number.</summary>
    internal static string Decoded(BarcodeFormat format, string text) => format switch
    {
        BarcodeFormat.Ean13 or BarcodeFormat.Ean8 or BarcodeFormat.UpcA or BarcodeFormat.UpcE or BarcodeFormat.Itf => text + CheckDigit(format, text),
        _ => text,
    };

    private static char CheckDigit(BarcodeFormat format, string digits)
    {
        if (format == BarcodeFormat.UpcE)
        {
            // UPC-E's check digit is the one of its UPC-A expansion.
            return CheckDigit(BarcodeFormat.UpcA, ZXing.OneD.UPCEReader.convertUPCEtoUPCA(digits + "0")[..11]);
        }

        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int d = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? d * 3 : d;
        }

        return (char)('0' + ((10 - (sum % 10)) % 10));
    }

    private static BitMatrix Encode(BarcodeFormat format, string text)
    {
        var zxingFormat = BarcodeFormatSet.ToZXing(format).Single();
        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.MARGIN] = 0,
            [EncodeHintType.DATA_MATRIX_SHAPE] = ZXing.Datamatrix.Encoder.SymbolShapeHint.FORCE_SQUARE,
        };
        var matrix = new MultiFormatWriter().encode(text, zxingFormat, 0, 0, hints);
        if (format is BarcodeFormat.QrCode or BarcodeFormat.DataMatrix or BarcodeFormat.Pdf417 or BarcodeFormat.Aztec)
        {
            return matrix;
        }

        // 1D writers repeat one row; keep a single row.
        var row = new BitMatrix(matrix.Width, 1);
        for (int x = 0; x < matrix.Width; x++)
        {
            if (matrix[x, 0])
            {
                row[x, 0] = true;
            }
        }

        return row;
    }
}
