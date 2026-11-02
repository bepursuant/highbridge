using System.Buffers.Binary;
using System.IO.Compression;
using Highbridge.Imaging;

namespace Highbridge.Core.Tests;

public class PngEncoderTests
{
    [Fact]
    public void Encodes_valid_rgb_png_with_correct_header_and_pixels()
    {
        byte[] pixels = [255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30];
        byte[] png = PngEncoder.Encode(2, 2, PngPixelFormat.Rgb24, pixels);

        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
        Assert.True(PngEncoder.TryReadSize(png, out int w, out int h));
        Assert.Equal((2, 2), (w, h));
        Assert.Equal(2, png[25]); // color type RGB

        var chunks = ReadChunks(png);
        Assert.Equal(["IHDR", "IDAT", "IEND"], chunks.Select(c => c.Type));
        foreach (var chunk in chunks)
        {
            Assert.Equal(chunk.Crc, PngEncoder.Crc32(System.Text.Encoding.ASCII.GetBytes(chunk.Type), chunk.Data));
        }

        using var zlib = new ZLibStream(new MemoryStream(chunks[1].Data), CompressionMode.Decompress);
        using var raw = new MemoryStream();
        zlib.CopyTo(raw);
        Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 10, 20, 30 }, raw.ToArray());
    }

    [Fact]
    public void Crc_matches_known_value_for_iend()
    {
        Assert.Equal(0xAE426082u, PngEncoder.Crc32("IEND"u8, []));
    }

    [Fact]
    public void Rejects_wrong_pixel_buffer_size()
    {
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(2, 2, PngPixelFormat.Gray8, new byte[3]));
    }

    private static List<(string Type, byte[] Data, uint Crc)> ReadChunks(byte[] png)
    {
        var chunks = new List<(string, byte[], uint)>();
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            string type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            byte[] data = png.AsSpan(offset + 8, length).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            chunks.Add((type, data, crc));
            offset += 12 + length;
        }

        return chunks;
    }
}
