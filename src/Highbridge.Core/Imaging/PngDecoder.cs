using System.Buffers.Binary;
using System.IO.Compression;

namespace Highbridge.Imaging;

/// <summary>
/// A decoded, non-interlaced PNG kept in its source sample layout (rows unfiltered, no filter
/// bytes). <see cref="ReadRow"/> converts one row at a time to 8-bit gray or RGB, so a large page
/// is never held twice.
/// </summary>
public sealed class PngImage
{
    private readonly byte[] _rows;
    private readonly int _stride;
    private readonly byte[]? _palette;
    private readonly byte[]? _paletteAlpha;

    internal PngImage(int width, int height, int bitDepth, int colorType, byte[] rows, int stride, byte[]? palette, byte[]? paletteAlpha, int dpi)
    {
        Width = width;
        Height = height;
        BitDepth = bitDepth;
        ColorType = colorType;
        _rows = rows;
        _stride = stride;
        _palette = palette;
        _paletteAlpha = paletteAlpha;
        Dpi = dpi;
        IsGray = colorType is 0 or 4 || (colorType == 3 && PaletteIsGray(palette!));
    }

    public int Width { get; }

    public int Height { get; }

    public int BitDepth { get; }

    /// <summary>PNG color type: 0 gray, 2 RGB, 3 palette, 4 gray+alpha, 6 RGBA.</summary>
    public int ColorType { get; }

    /// <summary>Horizontal resolution from the pHYs chunk, or 0 when the file does not say.</summary>
    public int Dpi { get; }

    /// <summary>True when every pixel converts to a gray value (gray types, or a palette of grays).</summary>
    public bool IsGray { get; }

    /// <summary>Channels of <see cref="ReadRow"/> output: 1 for gray, 3 for RGB.</summary>
    public int Channels => IsGray ? 1 : 3;

    /// <summary>
    /// Writes row <paramref name="y"/> as 8-bit samples (<see cref="Channels"/> per pixel). Alpha is
    /// composited onto white; 16-bit samples keep their high byte.
    /// </summary>
    public void ReadRow(int y, Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, Height);
        var row = _rows.AsSpan(y * _stride, _stride);
        int channels = Channels;
        for (int x = 0; x < Width; x++)
        {
            var (r, g, b, a) = Pixel(row, x);
            if (a != 255)
            {
                r = Blend(r, a);
                g = Blend(g, a);
                b = Blend(b, a);
            }

            if (channels == 1)
            {
                destination[x] = r;
            }
            else
            {
                destination[(x * 3)] = r;
                destination[(x * 3) + 1] = g;
                destination[(x * 3) + 2] = b;
            }
        }
    }

    private (byte R, byte G, byte B, byte A) Pixel(ReadOnlySpan<byte> row, int x)
    {
        switch (ColorType)
        {
            case 0:
            {
                byte v = Sample(row, x, 0, 1);
                return (v, v, v, 255);
            }

            case 2:
                return (Sample(row, x, 0, 3), Sample(row, x, 1, 3), Sample(row, x, 2, 3), 255);
            case 3:
            {
                int index = PackedIndex(row, x);
                if ((index * 3) + 2 >= _palette!.Length)
                {
                    throw new InvalidDataException("PNG palette index out of range.");
                }

                byte alpha = _paletteAlpha is not null && index < _paletteAlpha.Length ? _paletteAlpha[index] : (byte)255;
                return (_palette[index * 3], _palette[(index * 3) + 1], _palette[(index * 3) + 2], alpha);
            }

            case 4:
            {
                byte v = Sample(row, x, 0, 2);
                return (v, v, v, Sample(row, x, 1, 2));
            }

            default:
                return (Sample(row, x, 0, 4), Sample(row, x, 1, 4), Sample(row, x, 2, 4), Sample(row, x, 3, 4));
        }
    }

    /// <summary>One sample scaled to 8 bits (high byte for 16-bit, replicated bits below 8).</summary>
    private byte Sample(ReadOnlySpan<byte> row, int x, int channel, int channels)
    {
        switch (BitDepth)
        {
            case 8:
                return row[(x * channels) + channel];
            case 16:
                return row[((x * channels) + channel) * 2];
            default:
            {
                int raw = PackedIndex(row, x);
                int max = (1 << BitDepth) - 1;
                return (byte)(raw * 255 / max);
            }
        }
    }

    private int PackedIndex(ReadOnlySpan<byte> row, int x)
    {
        if (BitDepth == 8)
        {
            return row[x];
        }

        int bitOffset = x * BitDepth;
        int shift = 8 - BitDepth - (bitOffset % 8);
        return (row[bitOffset / 8] >> shift) & ((1 << BitDepth) - 1);
    }

    private static byte Blend(byte value, byte alpha) => (byte)(((value * alpha) + (255 * (255 - alpha)) + 127) / 255);

    private static bool PaletteIsGray(byte[] palette)
    {
        for (int i = 0; i + 2 < palette.Length; i += 3)
        {
            if (palette[i] != palette[i + 1] || palette[i] != palette[i + 2])
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Minimal dependency-free PNG reader (all color types and bit depths, no interlacing).</summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Upper bound for width x height, so a corrupt header cannot exhaust memory.</summary>
    private const long MaxPixels = 120_000_000;

    public static PngImage Decode(ReadOnlySpan<byte> png)
    {
        if (png.Length < Signature.Length || !png[..Signature.Length].SequenceEqual(Signature))
        {
            throw new InvalidDataException("Not a PNG file.");
        }

        int width = 0, height = 0, bitDepth = 0, colorType = -1, dpi = 0;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        int offset = Signature.Length;
        bool sawEnd = false;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12L + length > png.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk.");
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png[(offset + 8 + length)..]);
            if (crc != PngEncoder.Crc32(type, data))
            {
                throw new InvalidDataException($"Bad CRC in PNG chunk {System.Text.Encoding.ASCII.GetString(type)}.");
            }

            offset += 12 + length;
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                bitDepth = data[8];
                colorType = data[9];
                if (data[10] != 0 || data[11] != 0)
                {
                    throw new InvalidDataException("Unsupported PNG compression or filter method.");
                }

                if (data[12] != 0)
                {
                    throw new NotSupportedException("Interlaced PNGs are not supported.");
                }
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                palette = data.ToArray();
            }
            else if (type.SequenceEqual("tRNS"u8) && colorType == 3)
            {
                paletteAlpha = data.ToArray();
            }
            else if (type.SequenceEqual("pHYs"u8) && length == 9 && data[8] == 1)
            {
                // Pixels per metre; round to whole dots per inch.
                dpi = (int)Math.Round(BinaryPrimitives.ReadUInt32BigEndian(data) * 0.0254);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                sawEnd = true;
                break;
            }
        }

        if (!sawEnd || width <= 0 || height <= 0)
        {
            throw new InvalidDataException("Incomplete PNG.");
        }

        if ((long)width * height > MaxPixels)
        {
            throw new NotSupportedException($"A {width} x {height} page is too large to process.");
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException($"Unknown PNG color type {colorType}."),
        };
        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth)
        {
            throw new InvalidDataException($"Invalid bit depth {bitDepth} for PNG color type {colorType}.");
        }

        if (colorType == 3 && palette is null)
        {
            throw new InvalidDataException("Palette PNG without a PLTE chunk.");
        }

        int bitsPerPixel = channels * bitDepth;
        int stride = (int)(((long)width * bitsPerPixel + 7) / 8);
        int bytesPerPixel = Math.Max(1, bitsPerPixel / 8);
        var rows = new byte[(long)stride * height];

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        var previous = new byte[stride];
        var current = new byte[stride];
        Span<byte> filter = stackalloc byte[1];
        for (int y = 0; y < height; y++)
        {
            zlib.ReadExactly(filter);
            zlib.ReadExactly(current);
            Unfilter(filter[0], current, previous, bytesPerPixel);
            current.CopyTo(rows, (long)y * stride);
            (previous, current) = (current, previous);
        }

        return new PngImage(width, height, bitDepth, colorType, rows, stride, palette, paletteAlpha, dpi);
    }

    /// <summary>
    /// 8-bit luminance for barcode detection. Gray pages keep their samples. Color pages use
    /// Rec.601 (0.299, 0.587, 0.114) after alpha has been composited onto white.
    /// </summary>
    public static LuminanceImage DecodeLuminance(ReadOnlySpan<byte> png)
    {
        var image = Decode(png);
        var pixels = new byte[image.Width * image.Height];
        if (image.Channels == 1)
        {
            for (int y = 0; y < image.Height; y++)
            {
                image.ReadRow(y, pixels.AsSpan(y * image.Width, image.Width));
            }
        }
        else
        {
            var row = new byte[image.Width * 3];
            for (int y = 0; y < image.Height; y++)
            {
                image.ReadRow(y, row);
                int dest = y * image.Width;
                for (int x = 0; x < image.Width; x++)
                {
                    int i = x * 3;
                    pixels[dest + x] = (byte)(((row[i] * 299) + (row[i + 1] * 587) + (row[i + 2] * 114)) / 1000);
                }
            }
        }

        return new LuminanceImage(image.Width, image.Height, pixels);
    }

    private static void Unfilter(byte filter, Span<byte> row, ReadOnlySpan<byte> prior, int bpp)
    {
        switch (filter)
        {
            case 0:
                return;
            case 1:
                for (int i = bpp; i < row.Length; i++)
                {
                    row[i] += row[i - bpp];
                }

                return;
            case 2:
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] += prior[i];
                }

                return;
            case 3:
                for (int i = 0; i < row.Length; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    row[i] += (byte)((left + prior[i]) / 2);
                }

                return;
            case 4:
                for (int i = 0; i < row.Length; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    row[i] += (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
                }

                return;
            default:
                throw new InvalidDataException($"Unknown PNG filter type {filter}.");
        }
    }
}
