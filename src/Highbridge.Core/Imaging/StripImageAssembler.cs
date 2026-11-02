namespace Highbridge.Imaging;

/// <summary>
/// Builds one page from the uncompressed strips of a TWAIN memory transfer (ICAP_XFERMECH =
/// TWSX_MEMORY): rows top-down, samples in R,G,B order, each row padded to BytesPerRow. Supports
/// 1-bit black and white, 8/16-bit gray and 24/48-bit RGB (16-bit samples little-endian).
/// </summary>
public sealed class StripImageAssembler
{
    private readonly int _width;
    private readonly int _bitsPerPixel;
    private readonly bool _zeroIsBlack;
    private readonly bool _color;
    private readonly List<byte[]?> _rows = [];

    /// <param name="zeroIsBlack">TWAIN ICAP_PIXELFLAVOR: true for TWPF_CHOCOLATE (the default), false for TWPF_VANILLA.</param>
    public StripImageAssembler(int width, int bitsPerPixel, bool zeroIsBlack = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        if (bitsPerPixel is not (1 or 8 or 16 or 24 or 48))
        {
            throw new NotSupportedException($"{bitsPerPixel}-bit memory transfers are not supported.");
        }

        _width = width;
        _bitsPerPixel = bitsPerPixel;
        _zeroIsBlack = zeroIsBlack;
        _color = bitsPerPixel is 24 or 48;
    }

    public int Width => _width;

    public int Height => _rows.Count;

    public bool IsEmpty => _rows.Count == 0;

    public void AddStrip(int xOffset, int yOffset, int columns, int rows, int bytesPerRow, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(xOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(yOffset);
        int sourceRowBytes = ((columns * _bitsPerPixel) + 7) / 8;
        if (bytesPerRow < sourceRowBytes || data.Length < bytesPerRow * (rows - 1) + sourceRowBytes)
        {
            throw new ArgumentException("The strip is smaller than its rows and columns say.", nameof(data));
        }

        int channels = _color ? 3 : 1;
        int visibleColumns = Math.Min(columns, _width - xOffset);
        for (int r = 0; r < rows; r++)
        {
            int y = yOffset + r;
            while (_rows.Count <= y)
            {
                _rows.Add(null);
            }

            var target = _rows[y] ??= NewRow(channels);
            var source = data.Slice(r * bytesPerRow, sourceRowBytes);
            for (int x = 0; x < visibleColumns; x++)
            {
                int t = (xOffset + x) * channels;
                for (int c = 0; c < channels; c++)
                {
                    target[t + c] = Sample(source, x, c);
                }
            }
        }
    }

    public byte[] ToPng()
    {
        if (_rows.Count == 0)
        {
            throw new InvalidOperationException("No image data was received.");
        }

        int channels = _color ? 3 : 1;
        int stride = _width * channels;
        var pixels = new byte[stride * _rows.Count];
        for (int y = 0; y < _rows.Count; y++)
        {
            (_rows[y] ?? NewRow(channels)).CopyTo(pixels, y * stride);
        }

        return PngEncoder.Encode(_width, _rows.Count, _color ? PngPixelFormat.Rgb24 : PngPixelFormat.Gray8, pixels);
    }

    private byte[] NewRow(int channels)
    {
        var row = new byte[_width * channels];
        Array.Fill(row, (byte)255);
        return row;
    }

    private byte Sample(ReadOnlySpan<byte> row, int x, int channel)
    {
        byte value = _bitsPerPixel switch
        {
            1 => (row[x / 8] & (0x80 >> (x % 8))) != 0 ? (byte)255 : (byte)0,
            8 => row[x],
            16 => row[(x * 2) + 1],
            24 => row[(x * 3) + channel],
            _ => row[(x * 6) + (channel * 2) + 1],
        };
        return _zeroIsBlack ? value : (byte)(255 - value);
    }
}
