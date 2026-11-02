using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Highbridge.Imaging;

namespace Highbridge.Core.Tests;

public class PngDecoderTests
{
    [Theory]
    [InlineData(PngPixelFormat.Gray8)]
    [InlineData(PngPixelFormat.Rgb24)]
    public void Round_trips_what_the_encoder_writes(PngPixelFormat format)
    {
        int channels = format == PngPixelFormat.Rgb24 ? 3 : 1;
        var pixels = new byte[5 * 3 * channels];
        new Random(7).NextBytes(pixels);

        var image = PngDecoder.Decode(PngEncoder.Encode(5, 3, format, pixels, dpi: 300));

        Assert.Equal((5, 3, channels, 300), (image.Width, image.Height, image.Channels, image.Dpi));
        Assert.Equal(pixels, ReadAll(image));
    }

    [Fact]
    public void Bilevel_output_is_one_bit_and_decodes_to_black_and_white()
    {
        byte[] pixels = [0, 255, 255, 0, 0, 0, 255, 255, 0, 255, 0, 255, 255, 255, 255, 0, 0, 0];
        byte[] png = PngEncoder.Encode(9, 2, PngPixelFormat.Bilevel, pixels);

        Assert.Equal(1, png[24]); // IHDR bit depth
        Assert.Equal(0, png[25]); // gray
        Assert.Equal(pixels, ReadAll(PngDecoder.Decode(png)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Undoes_every_filter_type(byte filter)
    {
        var rgb = new byte[7 * 4 * 3];
        new Random(filter + 1).NextBytes(rgb);
        byte[] png = RawPng(7, 4, bitDepth: 8, colorType: 2, rgb, filter);

        Assert.Equal(rgb, ReadAll(PngDecoder.Decode(png)));
    }

    [Fact]
    public void Expands_gray_and_color_palettes()
    {
        // 2-bit palette: black, white, gray.
        byte[] grayPalette = [0, 0, 0, 255, 255, 255, 128, 128, 128];
        byte[] row = [0b00_01_10_01];
        var gray = PngDecoder.Decode(RawPng(4, 1, 2, 3, row, 0, grayPalette));
        Assert.True(gray.IsGray);
        Assert.Equal(new byte[] { 0, 255, 128, 255 }, ReadAll(gray));

        byte[] colorPalette = [255, 0, 0, 0, 0, 255];
        var color = PngDecoder.Decode(RawPng(2, 1, 8, 3, [1, 0], 0, colorPalette));
        Assert.False(color.IsGray);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0 }, ReadAll(color));
    }

    [Fact]
    public void Composites_alpha_on_white_and_keeps_the_high_byte_of_16_bit_samples()
    {
        // RGBA: opaque red, fully transparent.
        var rgba = PngDecoder.Decode(RawPng(2, 1, 8, 6, [255, 0, 0, 255, 0, 0, 0, 0], 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255, 255, 255 }, ReadAll(rgba));

        var gray16 = PngDecoder.Decode(RawPng(2, 1, 16, 0, [0x12, 0x34, 0xAB, 0xCD], 0));
        Assert.Equal(new byte[] { 0x12, 0xAB }, ReadAll(gray16));
    }

    [Fact]
    public void Rejects_corrupt_files()
    {
        byte[] png = PngEncoder.Encode(2, 2, PngPixelFormat.Gray8, new byte[4]);
        png[^20] ^= 0xFF; // inside IDAT

        Assert.ThrowsAny<InvalidDataException>(() => PngDecoder.Decode(png));
        Assert.ThrowsAny<InvalidDataException>(() => PngDecoder.Decode("not a png"u8));
    }

    [Fact]
    public void Writes_and_reads_latin1_and_utf8_text_chunks()
    {
        byte[] png = PngEncoder.Encode(1, 1, PngPixelFormat.Gray8, (_, row) => row[0] = 9, text:
        [
            new PngText("Software", "highbridge"),
            new PngText("Source", "Scanner für Büro 東京", Utf8: true),
        ]);

        var text = PngEncoder.ReadText(png);

        Assert.Equal(new PngText("Software", "highbridge"), text[0]);
        Assert.Equal(new PngText("Source", "Scanner für Büro 東京", Utf8: true), text[1]);
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(1, 1, PngPixelFormat.Gray8, (_, _) => { }, text: [new PngText("Bad", "東")]));
    }

    internal static byte[] ReadAll(PngImage image)
    {
        int stride = image.Width * image.Channels;
        var all = new byte[stride * image.Height];
        for (int y = 0; y < image.Height; y++)
        {
            image.ReadRow(y, all.AsSpan(y * stride, stride));
        }

        return all;
    }

    /// <summary>Builds a PNG from unfiltered rows, applying one filter type to every row.</summary>
    internal static byte[] RawPng(int width, int height, int bitDepth, int colorType, byte[] rows, byte filter, byte[]? palette = null)
    {
        int channels = colorType switch { 2 => 3, 4 => 2, 6 => 4, _ => 1 };
        int bitsPerPixel = channels * bitDepth;
        int stride = ((width * bitsPerPixel) + 7) / 8;
        int bpp = Math.Max(1, bitsPerPixel / 8);

        using var raw = new MemoryStream();
        using (var zlib = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var prior = new byte[stride];
            for (int y = 0; y < height; y++)
            {
                var row = rows.AsSpan(y * stride, stride);
                zlib.WriteByte(filter);
                for (int i = 0; i < stride; i++)
                {
                    int a = i >= bpp ? row[i - bpp] : 0, b = prior[i], c = i >= bpp ? prior[i - bpp] : 0;
                    int predictor = filter switch
                    {
                        1 => a,
                        2 => b,
                        3 => (a + b) / 2,
                        4 => Paeth(a, b, c),
                        _ => 0,
                    };
                    zlib.WriteByte((byte)(row[i] - predictor));
                }

                row.CopyTo(prior);
            }
        }

        using var png = new MemoryStream();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        Chunk(png, "IHDR", header);
        if (palette is not null)
        {
            Chunk(png, "PLTE", palette);
        }

        Chunk(png, "IDAT", raw.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, PngEncoder.Crc32(typeBytes, data));
        output.Write(crc);
    }
}
