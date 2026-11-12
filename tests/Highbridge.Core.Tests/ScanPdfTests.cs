using PdfSharp.Pdf.IO;
using Highbridge.Imaging;
using Highbridge.Scanning;

namespace Highbridge.Core.Tests;

public class ScanPdfTests
{
    [Fact]
    public void Pages_keep_their_order_and_a_later_document_is_separate()
    {
        var front = Page(40, 60, grayscale: false, threshold: false, dpi: 100);
        var back = Page(80, 50, grayscale: true, threshold: true, dpi: 200);

        byte[] first = ScanPdf.Create([front, back], fallbackDpi: 150);
        byte[] second = ScanPdf.Create([front], fallbackDpi: 150);

        using var frontAndBack = Open(first);
        using var frontOnly = Open(second);

        Assert.Equal(2, frontAndBack.PageCount);
        Assert.Equal(28.8, frontAndBack.Pages[0].Width.Point, precision: 2);
        Assert.Equal(43.2, frontAndBack.Pages[0].Height.Point, precision: 2);
        Assert.Equal(28.8, frontAndBack.Pages[1].Width.Point, precision: 2);
        Assert.Equal(18.0, frontAndBack.Pages[1].Height.Point, precision: 2);
        Assert.Equal(1, frontOnly.PageCount);
        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Color_gray_and_black_and_white_pages_become_a_pdf(bool grayscale, bool threshold)
    {
        byte[] pdf = ScanPdf.Create([Page(30, 40, grayscale, threshold, dpi: 72)], fallbackDpi: 72);

        Assert.Equal("%PDF"u8.ToArray(), pdf[..4]);
        using var document = Open(pdf);
        Assert.Equal(1, document.PageCount);
        Assert.Equal(30, document.Pages[0].Width.Point, precision: 2);
        Assert.Equal(40, document.Pages[0].Height.Point, precision: 2);
    }

    private static ScannedPage Page(int width, int height, bool grayscale, bool threshold, int dpi)
    {
        var canvas = new Canvas(width, height);
        canvas.FillRect(0, 0, width, height, (20, 40, 80));
        canvas.FillRect(1, 1, Math.Max(1, width / 2), Math.Max(1, height / 2), (220, 220, 220));
        return new ScannedPage(0, OutputFormats.PngMediaType, width, height, canvas.ToPng(grayscale, threshold, dpi));
    }

    private static PdfSharp.Pdf.PdfDocument Open(byte[] pdf) =>
        PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
}
