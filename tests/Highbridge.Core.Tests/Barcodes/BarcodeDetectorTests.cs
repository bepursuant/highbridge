using Highbridge.Barcodes;
using Highbridge.Imaging;

namespace Highbridge.Core.Tests.Barcodes;

public class BarcodeDetectorTests
{
    private const int Dpi = 200;
    private static readonly (byte, byte, byte) Black = (0, 0, 0);
    private static readonly (byte, byte, byte) White = (255, 255, 255);

    public static TheoryData<BarcodeFormat, int> EveryFormatAtEveryQuarterTurn()
    {
        var data = new TheoryData<BarcodeFormat, int>();
        foreach (var format in BarcodeFormatSet.Split(BarcodeFormatSet.All))
        {
            foreach (int angle in BarcodeDetector.Rotations)
            {
                data.Add(format, angle);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryFormatAtEveryQuarterTurn))]
    public void Finds_each_format_upright_rotated_and_upside_down(BarcodeFormat format, int angle)
    {
        string text = SampleText(format);
        var page = Page(900, 1100, canvas => MockBarcodeSheet.DrawCode(canvas, format, text, 400, 600, angle, Dpi, Black));

        var detection = BarcodeDetector.Detect(page, pageIndex: 2, BarcodeFormatSet.All, TimeSpan.FromSeconds(30));

        var barcode = Assert.Single(detection.Barcodes);
        Assert.Equal((format, text, 2), (barcode.Format, barcode.Text, barcode.Page));
        // Aztec's points are the corners of its bull's eye expanded on the image diagonals, so they
        // don't show which way the symbol is turned.
        if (format == BarcodeFormat.Aztec)
        {
            Assert.Null(barcode.Orientation);
        }
        else
        {
            Assert.Equal(angle, barcode.Orientation);
        }
        // 1D codes are read along pixel rows: upright and upside down in the 0-degree pass, turned ones in the 90-degree pass.
        bool linear = (format & BarcodeDetector.AnyAngle) == 0;
        Assert.Equal(linear && angle % 180 == 90 ? 90 : 0, barcode.Rotation);
        AssertCenteredOn(barcode, 400, 600);
        Assert.Equal(BarcodeDetectionOutcome.Complete, detection.Outcome);
    }

    [Theory]
    [InlineData(BarcodeFormat.QrCode, 33)]
    [InlineData(BarcodeFormat.QrCode, 200)]
    [InlineData(BarcodeFormat.DataMatrix, 15)]
    [InlineData(BarcodeFormat.DataMatrix, 350)]
    [InlineData(BarcodeFormat.Pdf417, 6)]
    [InlineData(BarcodeFormat.Pdf417, 184)]
    [InlineData(BarcodeFormat.Code128, 7)]
    [InlineData(BarcodeFormat.Code128, 263)]
    [InlineData(BarcodeFormat.Ean13, 354)]
    public void Finds_skewed_codes(BarcodeFormat format, int angle)
    {
        string text = SampleText(format);
        var page = Page(900, 1100, canvas => MockBarcodeSheet.DrawCode(canvas, format, text, 450, 550, angle, Dpi, Black));

        var barcode = Assert.Single(BarcodeDetector.Detect(page, 0, BarcodeFormatSet.All, TimeSpan.FromSeconds(30)).Barcodes);

        Assert.Equal(text, barcode.Text);
        // 1D points lie on one pixel row, so their orientation is the nearest quarter turn.
        int expected = barcode.Points.Count == 2 ? (int)(Math.Round(angle / 90.0) * 90) % 360 : angle;
        Assert.InRange(AngleBetween(barcode.Orientation!.Value, expected), 0, 3);
        AssertCenteredOn(barcode, 450, 550);
    }

    [Fact]
    public void Finds_light_on_dark_codes()
    {
        var page = Page(700, 700, canvas =>
        {
            canvas.FillRect(0, 0, 700, 700, (30, 30, 30));
            MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.QrCode, "INVERTED", 350, 350, 0, Dpi, White);
        });

        var barcode = Assert.Single(BarcodeDetector.Detect(page, 0, BarcodeFormatSet.Default, TimeSpan.FromSeconds(30)).Barcodes);

        Assert.Equal("INVERTED", barcode.Text);
    }

    [Theory]
    [InlineData(200, 0)]
    [InlineData(200, 1)]
    [InlineData(200, 2)]
    [InlineData(300, 0)]
    [InlineData(300, 1)]
    [InlineData(300, 2)]
    public void Reads_every_code_on_the_mock_test_sheets(int dpi, int pageIndex)
    {
        var page = PngDecoder.DecodeLuminance(MockBarcodeSheet.RenderPng(pageIndex, 3, dpi));

        var detection = BarcodeDetector.Detect(page, pageIndex, BarcodeFormatSet.Default, TimeSpan.FromSeconds(60));

        var expected = MockBarcodeSheet.Contents(pageIndex);
        Assert.Equal(
            expected.Select(c => (c.Format, c.Text)).OrderBy(c => c.Text),
            detection.Barcodes.Select(b => (b.Format, b.Text)).OrderBy(c => c.Text));
        foreach (var code in expected.Where(c => c.Angle % 90 == 0))
        {
            int orientation = detection.Barcodes.Single(b => b.Text == code.Text).Orientation!.Value;
            Assert.InRange(AngleBetween(orientation, code.Angle), 0, 2);
        }
    }

    [Fact]
    public void Restricting_formats_skips_the_others()
    {
        var page = Page(900, 700, canvas =>
        {
            MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.QrCode, "KEEP-QR", 220, 350, 0, Dpi, Black);
            MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.Ean13, MockBarcodeSheet.Decoded(BarcodeFormat.Ean13, "400638133393"), 650, 350, 0, Dpi, Black);
            MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.Code128, "DROP-ME", 450, 120, 0, Dpi, Black);
        });

        var detection = BarcodeDetector.Detect(page, 0, BarcodeFormat.QrCode | BarcodeFormat.Ean13, TimeSpan.FromSeconds(30));

        Assert.Equal(new[] { "4006381333931", "KEEP-QR" }.Order(), detection.Barcodes.Select(b => b.Text).Order());
    }

    [Fact]
    public void A_page_without_codes_has_none()
    {
        var page = Page(1654, 2339, canvas =>
        {
            for (int y = 200; y < 2100; y += 40)
            {
                canvas.DrawText("THE QUICK BROWN FOX JUMPS OVER THE LAZY DOG 0123456789", 150, y, 4, Black);
            }
        });

        var detection = BarcodeDetector.Detect(page, 0, BarcodeFormatSet.All, TimeSpan.FromSeconds(30));

        Assert.Empty(detection.Barcodes);
        Assert.Equal(4, detection.Passes);
    }

    [Fact]
    public void Stops_after_the_first_pass_when_the_budget_is_spent()
    {
        var page = Page(800, 800, canvas => MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.QrCode, "FIRST PASS", 400, 400, 0, Dpi, Black));

        var detection = BarcodeDetector.Detect(page, 0, BarcodeFormatSet.Default, TimeSpan.Zero);

        Assert.Equal(BarcodeDetectionOutcome.Partial, detection.Outcome);
        Assert.Equal(1, detection.Passes);
        Assert.Equal("FIRST PASS", Assert.Single(detection.Barcodes).Text);
    }

    [Fact]
    public void Shrinks_very_large_pages_and_reports_full_size_coordinates()
    {
        // About A4 at 480 dpi: above the limit, so it is searched at half size.
        var page = Page(4000, 5600, canvas => MockBarcodeSheet.DrawCode(canvas, BarcodeFormat.Code128, "BIG PAGE", 3000, 4200, 90, 480, Black));
        Assert.True((long)page.Width * page.Height > BarcodeDetector.MaxPixels);

        var barcode = Assert.Single(BarcodeDetector.Detect(page, 0, BarcodeFormatSet.Default, TimeSpan.FromSeconds(30)).Barcodes);

        Assert.Equal(("BIG PAGE", 90), (barcode.Text, barcode.Orientation));
        AssertCenteredOn(barcode, 3000, 4200, tolerance: 130);
    }

    private static LuminanceImage Page(int width, int height, Action<Canvas> draw)
    {
        var canvas = new Canvas(width, height);
        draw(canvas);
        return PngDecoder.DecodeLuminance(canvas.ToPng(grayscale: true, threshold: false));
    }

    private static string SampleText(BarcodeFormat format) => format switch
    {
        BarcodeFormat.Ean13 => MockBarcodeSheet.Decoded(format, "590123412345"),
        BarcodeFormat.Ean8 => MockBarcodeSheet.Decoded(format, "9638507"),
        BarcodeFormat.UpcA => MockBarcodeSheet.Decoded(format, "04210000526"),
        BarcodeFormat.UpcE => MockBarcodeSheet.Decoded(format, "0425261"),
        BarcodeFormat.Itf => MockBarcodeSheet.Decoded(format, "0001234560001"),
        BarcodeFormat.Codabar => "40156",
        BarcodeFormat.Code39 or BarcodeFormat.Code93 or BarcodeFormat.Code128 => "PT-12345",
        _ => $"highbridge {BarcodeFormatSet.Name(format)}",
    };

    /// <summary>
    /// Points mark the code's corners, or for 1D codes the ends of the pixel row it was read on
    /// (anywhere along the bars' height), so their middle is near the code's middle.
    /// </summary>
    private static void AssertCenteredOn(BarcodeResult barcode, double x, double y, double tolerance = 60)
    {
        double cx = (barcode.Points.Min(p => p.X) + barcode.Points.Max(p => p.X)) / 2;
        double cy = (barcode.Points.Min(p => p.Y) + barcode.Points.Max(p => p.Y)) / 2;
        Assert.InRange(cx, x - tolerance, x + tolerance);
        Assert.InRange(cy, y - tolerance, y + tolerance);
    }

    private static int AngleBetween(int a, int b) => Math.Min(Math.Abs(a - b), 360 - Math.Abs(a - b));
}
