using System.Buffers.Binary;
using System.IO.Compression;
using Highbridge.Imaging;

namespace Highbridge.Core.Tests;

public class StripImageAssemblerTests
{
    [Fact]
    public void Joins_padded_rgb_strips_top_down()
    {
        var image = new StripImageAssembler(width: 2, bitsPerPixel: 24);
        // Rows are 6 bytes of pixels padded to 8.
        image.AddStrip(0, 0, 2, 1, 8, [1, 2, 3, 4, 5, 6, 0xEE, 0xEE]);
        image.AddStrip(0, 1, 2, 1, 8, [7, 8, 9, 10, 11, 12, 0xEE, 0xEE]);

        Assert.Equal((2, 2), (image.Width, image.Height));
        var (width, height, colorType, pixels) = Decode(image.ToPng());
        Assert.Equal((2, 2, 2), (width, height, colorType));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, pixels);
    }

    [Fact]
    public void Expands_black_and_white_bits_to_gray()
    {
        var image = new StripImageAssembler(width: 10, bitsPerPixel: 1);
        image.AddStrip(0, 0, 10, 1, 4, [0b1010_0000, 0b0100_0000, 0, 0]);

        var (_, _, colorType, pixels) = Decode(image.ToPng());
        Assert.Equal(0, colorType);
        Assert.Equal(new byte[] { 255, 0, 255, 0, 0, 0, 0, 0, 0, 255 }, pixels);
    }

    [Fact]
    public void Vanilla_pixel_flavor_is_inverted()
    {
        var image = new StripImageAssembler(width: 2, bitsPerPixel: 8, zeroIsBlack: false);
        image.AddStrip(0, 0, 2, 1, 2, [0, 200]);

        Assert.Equal(new byte[] { 255, 55 }, Decode(image.ToPng()).Pixels);
    }

    [Fact]
    public void Sixteen_bit_gray_keeps_the_high_byte()
    {
        var image = new StripImageAssembler(width: 2, bitsPerPixel: 16);
        image.AddStrip(0, 0, 2, 1, 4, [0x34, 0x12, 0xFF, 0xAB]);

        Assert.Equal(new byte[] { 0x12, 0xAB }, Decode(image.ToPng()).Pixels);
    }

    [Fact]
    public void Tiles_are_placed_at_their_offsets_and_gaps_stay_white()
    {
        var image = new StripImageAssembler(width: 4, bitsPerPixel: 8);
        image.AddStrip(2, 0, 2, 1, 2, [10, 20]);
        image.AddStrip(0, 2, 2, 1, 2, [30, 40]);

        Assert.Equal(3, image.Height);
        Assert.Equal(
            new byte[] { 255, 255, 10, 20, 255, 255, 255, 255, 30, 40, 255, 255 },
            Decode(image.ToPng()).Pixels);
    }

    [Fact]
    public void Rejects_strips_shorter_than_they_claim()
    {
        var image = new StripImageAssembler(width: 4, bitsPerPixel: 8);
        Assert.Throws<ArgumentException>(() => image.AddStrip(0, 0, 4, 2, 4, new byte[6]));
    }

    [Fact]
    public void Rejects_unsupported_bit_depths()
    {
        Assert.Throws<NotSupportedException>(() => new StripImageAssembler(4, 4));
    }

    private static (int Width, int Height, byte ColorType, byte[] Pixels) Decode(byte[] png)
    {
        int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
        int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
        byte colorType = png[25];
        int channels = colorType == 2 ? 3 : 1;

        using var idat = new MemoryStream();
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (System.Text.Encoding.ASCII.GetString(png, offset + 4, 4) == "IDAT")
            {
                idat.Write(png, offset + 8, length);
            }

            offset += 12 + length;
        }

        idat.Position = 0;
        using var zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);

        // PngEncoder writes filter type 0 (none) on every row.
        byte[] filtered = raw.ToArray();
        int stride = width * channels;
        var pixels = new byte[stride * height];
        for (int y = 0; y < height; y++)
        {
            Assert.Equal(0, filtered[y * (stride + 1)]);
            Array.Copy(filtered, (y * (stride + 1)) + 1, pixels, y * stride, stride);
        }

        return (width, height, colorType, pixels);
    }
}
