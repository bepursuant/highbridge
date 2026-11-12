using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Highbridge.Imaging;

namespace Highbridge.Scanning;

/// <summary>
/// One PDF for one scan job. Pages are embedded in the order they were scanned. A later job builds
/// its own document; nothing here keeps pages from a previous call.
/// </summary>
public static class ScanPdf
{
    public static byte[] Create(IReadOnlyList<ScannedPage> pages, int fallbackDpi)
    {
        if (pages.Count == 0)
        {
            throw new ScannerException("no_pages", "The scanner returned no pages. Is there paper in the feeder?");
        }

        var document = new PdfDocument();
        document.Info.Creator = "highbridge";
        var held = new List<IDisposable>(pages.Count * 2);
        try
        {
            foreach (var page in pages)
            {
                if (page.MediaType != OutputFormats.PngMediaType)
                {
                    throw new ScannerException("pdf_failed", $"Cannot build a PDF from {page.MediaType}.");
                }

                var (png, dpi, width, height) = PlainPng(page.Data, fallbackDpi);
                var stream = new MemoryStream(png, writable: false);
                held.Add(stream);
                var image = XImage.FromStream(stream);
                held.Add(image);

                var pdfPage = document.AddPage();
                double widthPt = width * 72.0 / dpi;
                double heightPt = height * 72.0 / dpi;
                pdfPage.Width = XUnit.FromPoint(widthPt);
                pdfPage.Height = XUnit.FromPoint(heightPt);
                using var gfx = XGraphics.FromPdfPage(pdfPage);
                gfx.DrawImage(image, 0, 0, widthPt, heightPt);
            }

            using var output = new MemoryStream();
            document.Save(output, false);
            return output.ToArray();
        }
        catch (ScannerException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ScannerException("pdf_failed", "highbridge could not build the PDF.", ex);
        }
        finally
        {
            foreach (var item in held)
            {
                item.Dispose();
            }

            document.Dispose();
        }
    }

    /// <summary>
    /// An 8-bit gray or RGB PNG with a pHYs chunk and no text chunks. PDFsharp embeds that;
    /// 1-bit pages and provenance text chunks stay in the pixels, not in the file structure.
    /// </summary>
    private static (byte[] Png, int Dpi, int Width, int Height) PlainPng(byte[] png, int fallbackDpi)
    {
        var image = PngDecoder.Decode(png);
        int dpi = image.Dpi > 0 ? image.Dpi : fallbackDpi > 0 ? fallbackDpi : 72;
        var format = image.IsGray ? PngPixelFormat.Gray8 : PngPixelFormat.Rgb24;
        byte[] plain = PngEncoder.Encode(image.Width, image.Height, format, image.ReadRow, dpi);
        return (plain, dpi, image.Width, image.Height);
    }
}
