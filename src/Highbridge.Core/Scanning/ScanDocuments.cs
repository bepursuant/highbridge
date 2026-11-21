using Highbridge.Imaging;

namespace Highbridge.Scanning;

/// <summary>
/// Turns scanner pages into documents: rotate each page, then stack each group into one PNG.
/// </summary>
public static class ScanDocuments
{
    public static IReadOnlyList<ScannedDocument> Build(IReadOnlyList<ScannedPage> pages, ScanOptions options)
    {
        if (pages.Count == 0)
        {
            return [];
        }

        var rotated = new List<ScannedPage>(pages.Count);
        foreach (var page in pages)
        {
            rotated.Add(Rotate(page, options.Orientation));
        }

        int size = options.MaxPagesPerDocument <= 0 ? rotated.Count : options.MaxPagesPerDocument;
        var documents = new List<ScannedDocument>();
        for (int start = 0; start < rotated.Count; start += size)
        {
            int count = Math.Min(size, rotated.Count - start);
            var group = rotated.GetRange(start, count);
            documents.Add(Stack(group, documents.Count));
        }

        return documents;
    }

    public static ScannedPage Rotate(ScannedPage page, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0)
        {
            return page;
        }

        var image = PngDecoder.Decode(page.Data);
        int channels = image.Channels;
        int width = image.Width;
        int height = image.Height;
        bool swap = degrees is 90 or 270;
        int destWidth = swap ? height : width;
        int destHeight = swap ? width : height;
        var source = new byte[height][];
        for (int y = 0; y < height; y++)
        {
            var row = new byte[width * channels];
            image.ReadRow(y, row);
            source[y] = row;
        }

        var pixels = new byte[destWidth * destHeight * channels];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int nx, ny;
                switch (degrees)
                {
                    case 90:
                        nx = height - 1 - y;
                        ny = x;
                        break;
                    case 180:
                        nx = width - 1 - x;
                        ny = height - 1 - y;
                        break;
                    default:
                        nx = y;
                        ny = width - 1 - x;
                        break;
                }

                int from = x * channels;
                int to = ((ny * destWidth) + nx) * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    pixels[to + channel] = source[y][from + channel];
                }
            }
        }

        var format = channels == 1 ? PngPixelFormat.Gray8 : PngPixelFormat.Rgb24;
        byte[] png = PngEncoder.Encode(destWidth, destHeight, format, pixels, image.Dpi);
        return new ScannedPage(page.Index, page.MediaType, destWidth, destHeight, png) { ScannerName = page.ScannerName };
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
