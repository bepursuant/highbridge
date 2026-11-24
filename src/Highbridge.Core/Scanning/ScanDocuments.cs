using Highbridge.Imaging;

namespace Highbridge.Scanning;

/// <summary>
/// Turns scanner pages into documents. The scanner has already oriented each page.
/// Pages are stacked into one PNG per document.
/// </summary>
public static class ScanDocuments
{
    public static IReadOnlyList<ScannedDocument> Build(IReadOnlyList<ScannedPage> pages, ScanOptions options)
    {
        if (pages.Count == 0)
        {
            return [];
        }

        // A flatbed is one image. Zero for both limits keeps the whole feeder in one document.
        if (options.Source == PaperSource.Flatbed || (options.MaxSheets <= 0 && options.MaxPagesPerDocument <= 0))
        {
            return [Stack(pages, 0)];
        }

        // Duplex front and back are one sheet. A document closes when either limit is reached.
        int pagesPerSheet = options.Duplex ? 2 : 1;
        int maxPages = options.MaxPagesPerDocument;
        int maxSheets = options.MaxSheets;
        var documents = new List<ScannedDocument>();
        var group = new List<ScannedPage>();
        int sheets = 0;
        foreach (var page in pages)
        {
            group.Add(page);
            bool closedSheet = group.Count % pagesPerSheet == 0;
            if (closedSheet)
            {
                sheets++;
            }

            bool pageLimit = maxPages > 0 && group.Count >= maxPages;
            bool sheetLimit = maxSheets > 0 && closedSheet && sheets >= maxSheets;
            if (pageLimit || sheetLimit)
            {
                documents.Add(Stack(group, documents.Count));
                group = [];
                sheets = 0;
            }
        }

        if (group.Count > 0)
        {
            documents.Add(Stack(group, documents.Count));
        }

        return documents;
    }

    public static ScannedDocument Stack(IReadOnlyList<ScannedPage> pages, int index)
    {
        var images = new PngImage[pages.Count];
        for (int i = 0; i < pages.Count; i++)
        {
            images[i] = PngDecoder.Decode(pages[i].Data);
        }

        bool color = false;
        int width = 1;
        int height = 0;
        int dpi = 0;
        foreach (var image in images)
        {
            color |= image.Channels == 3;
            width = Math.Max(width, image.Width);
            height += image.Height;
            if (dpi == 0 && image.Dpi > 0)
            {
                dpi = image.Dpi;
            }
        }

        int channels = color ? 3 : 1;
        var pixels = new byte[width * height * channels];
        Array.Fill(pixels, (byte)255);
        int top = 0;
        foreach (var image in images)
        {
            var row = new byte[image.Width * image.Channels];
            for (int y = 0; y < image.Height; y++)
            {
                image.ReadRow(y, row);
                int dest = (top + y) * width * channels;
                for (int x = 0; x < image.Width; x++)
                {
                    if (channels == 1)
                    {
                        pixels[dest + x] = row[x];
                    }
                    else if (image.Channels == 1)
                    {
                        byte value = row[x];
                        int at = dest + (x * 3);
                        pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                    }
                    else
                    {
                        int from = x * 3;
                        int at = dest + from;
                        pixels[at] = row[from];
                        pixels[at + 1] = row[from + 1];
                        pixels[at + 2] = row[from + 2];
                    }
                }
            }

            top += image.Height;
        }

        var format = channels == 1 ? PngPixelFormat.Gray8 : PngPixelFormat.Rgb24;
        byte[] png = PngEncoder.Encode(width, height, format, pixels, dpi);
        string? scanner = null;
        foreach (var page in pages)
        {
            if (!string.IsNullOrWhiteSpace(page.ScannerName))
            {
                scanner = page.ScannerName;
                break;
            }
        }

        return new ScannedDocument(index, "image/png", width, height, png, pages.Count) { ScannerName = scanner };
    }
}
