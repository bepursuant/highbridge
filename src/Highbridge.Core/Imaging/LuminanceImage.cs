namespace Highbridge.Imaging;

/// <summary>An 8-bit grayscale image, one byte per pixel, rows top-down.</summary>
public sealed class LuminanceImage
{
    public LuminanceImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    /// <summary>Returns a copy shrunk by <paramref name="factor"/> in both directions, averaging each block.</summary>
    public LuminanceImage Downscale(int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (factor == 1)
        {
            return this;
        }

        int w = Math.Max(1, Width / factor), h = Math.Max(1, Height / factor);
        var target = new byte[w * h];
        int area = factor * factor;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (((y * factor) + dy) * Width) + (x * factor);
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += Pixels[row + dx];
                    }
                }

                target[(y * w) + x] = (byte)(sum / area);
            }
        }

        return new LuminanceImage(w, h, target);
    }

    /// <summary>Returns a copy turned clockwise by 0, 90, 180 or 270 degrees.</summary>
    public LuminanceImage RotateClockwise(int degrees)
    {
        int w = Width, h = Height;
        var source = Pixels;
        switch (((degrees % 360) + 360) % 360)
        {
            case 0:
                return this;
            case 180:
            {
                var target = new byte[source.Length];
                for (int i = 0, j = source.Length - 1; i < source.Length; i++, j--)
                {
                    target[j] = source[i];
                }

                return new LuminanceImage(w, h, target);
            }

            case 90:
            {
                // Source (x, y) lands at (h - 1 - y, x) in an h-wide image.
                var target = new byte[source.Length];
                for (int y = 0; y < h; y++)
                {
                    int column = h - 1 - y;
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        target[(x * h) + column] = source[row + x];
                    }
                }

                return new LuminanceImage(h, w, target);
            }

            case 270:
            {
                // Source (x, y) lands at (y, w - 1 - x).
                var target = new byte[source.Length];
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    for (int x = 0; x < w; x++)
                    {
                        target[((w - 1 - x) * h) + y] = source[row + x];
                    }
                }

                return new LuminanceImage(h, w, target);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees are supported.");
        }
    }

    /// <summary>Maps a point of <see cref="RotateClockwise"/>(<paramref name="degrees"/>) back to this image.</summary>
    public (float X, float Y) FromRotated(int degrees, float x, float y) => (((degrees % 360) + 360) % 360) switch
    {
        0 => (x, y),
        90 => (y, Height - 1 - x),
        180 => (Width - 1 - x, Height - 1 - y),
        270 => (Width - 1 - y, x),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees), degrees, "Only multiples of 90 degrees are supported."),
    };
}
