using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Highbridge.Imaging;

public enum PngPixelFormat
{
    Gray8 = 0,
    Rgb24 = 2,

    /// <summary>Written as 1-bit gray. Rows are supplied as one byte per pixel; values below 128 are black.</summary>
    Bilevel = 100,
}

/// <summary>A PNG text chunk: tEXt (Latin-1) or, when <see cref="Utf8"/> is set, uncompressed iTXt.</summary>
public sealed record PngText(string Keyword, string Text, bool Utf8 = false);

/// <summary>Fills <paramref name="row"/> with row <paramref name="y"/> (one byte per sample).</summary>
public delegate void PngRowSource(int y, Span<byte> row);

/// <summary>Minimal dependency-free PNG writer (8-bit gray or RGB, or 1-bit gray; no interlacing).</summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(int width, int height, PngPixelFormat format, ReadOnlySpan<byte> pixels, int dpi = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        int stride = width * SamplesPerPixel(format);
        if (pixels.Length != stride * height)
        {
            throw new ArgumentException($"Expected {stride * height} bytes of pixel data, got {pixels.Length}.", nameof(pixels));
        }

        return EncodeCore(width, height, format, null, pixels, dpi, null);
    }

    /// <param name="dpi">Written as pHYs when positive, so the page prints at its real size.</param>
    /// <param name="text">Text chunks, written before the image data.</param>
    public static byte[] Encode(int width, int height, PngPixelFormat format, PngRowSource rows, int dpi = 0, IReadOnlyList<PngText>? text = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        return EncodeCore(width, height, format, rows, [], dpi, text);
    }

    private static byte[] EncodeCore(int width, int height, PngPixelFormat format, PngRowSource? rows, ReadOnlySpan<byte> pixels, int dpi, IReadOnlyList<PngText>? text)
    {
        int samples = width * SamplesPerPixel(format);
        bool bilevel = format == PngPixelFormat.Bilevel;

        using var output = new MemoryStream();
        output.Write(Signature);

        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = bilevel ? (byte)1 : (byte)8; // bit depth
        header[9] = format == PngPixelFormat.Rgb24 ? (byte)2 : (byte)0; // color type
        header[10] = 0; // deflate
        header[11] = 0; // adaptive filtering
        header[12] = 0; // no interlace
        WriteChunk(output, "IHDR", header);

        if (dpi > 0)
        {
            Span<byte> phys = stackalloc byte[9];
            uint perMetre = (uint)Math.Round(dpi / 0.0254);
            BinaryPrimitives.WriteUInt32BigEndian(phys, perMetre);
            BinaryPrimitives.WriteUInt32BigEndian(phys[4..], perMetre);
            phys[8] = 1; // unit: metre
            WriteChunk(output, "pHYs", phys);
        }

        foreach (var entry in text ?? [])
        {
            WriteText(output, entry);
        }

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[samples];
                var packed = bilevel ? new byte[(width + 7) / 8] : null;
                for (int y = 0; y < height; y++)
                {
                    if (rows is null)
                    {
                        pixels.Slice(y * samples, samples).CopyTo(row);
                    }
                    else
                    {
                        rows(y, row);
                    }

                    zlib.WriteByte(0); // filter: none
                    if (packed is null)
                    {
                        zlib.Write(row);
                        continue;
                    }

                    Array.Clear(packed);
                    for (int x = 0; x < width; x++)
                    {
                        if (row[x] >= 128)
                        {
                            packed[x >> 3] |= (byte)(0x80 >> (x & 7));
                        }
                    }

                    zlib.Write(packed);
                }
            }

            WriteChunk(output, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        }

        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>Reads the tEXt and iTXt chunks of a PNG (keyword, text), in file order.</summary>
    public static IReadOnlyList<PngText> ReadText(ReadOnlySpan<byte> png)
    {
        var result = new List<PngText>();
        int offset = Signature.Length;
        while (offset + 12 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12L + length > png.Length)
            {
                break;
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);
            offset += 12 + length;
            int nul = data.IndexOf((byte)0);
            if (nul <= 0)
            {
                continue;
            }

            string keyword = Encoding.Latin1.GetString(data[..nul]);
            if (type.SequenceEqual("tEXt"u8))
            {
                result.Add(new PngText(keyword, Encoding.Latin1.GetString(data[(nul + 1)..])));
            }
            else if (type.SequenceEqual("iTXt"u8) && data.Length > nul + 2 && data[nul + 1] == 0)
            {
                // keyword NUL, compression flag, method, language NUL, translated keyword NUL, text
                var rest = data[(nul + 3)..];
                int languageEnd = rest.IndexOf((byte)0);
                if (languageEnd < 0)
                {
                    continue;
                }

                rest = rest[(languageEnd + 1)..];
                int translatedEnd = rest.IndexOf((byte)0);
                if (translatedEnd < 0)
                {
                    continue;
                }

                result.Add(new PngText(keyword, Encoding.UTF8.GetString(rest[(translatedEnd + 1)..]), Utf8: true));
            }
        }

        return result;
    }

    private static int SamplesPerPixel(PngPixelFormat format) => format == PngPixelFormat.Rgb24 ? 3 : 1;

    private static void WriteText(Stream output, PngText entry)
    {
        if (entry.Keyword.Length is < 1 or > 79 || entry.Keyword.Any(c => c is < ' ' or > '~'))
        {
            throw new ArgumentException($"Invalid PNG text keyword '{entry.Keyword}'.", nameof(entry));
        }

        using var data = new MemoryStream();
        data.Write(Encoding.Latin1.GetBytes(entry.Keyword));
        data.WriteByte(0);
        if (entry.Utf8)
        {
            data.WriteByte(0); // not compressed
            data.WriteByte(0); // compression method
            data.WriteByte(0); // no language tag
            data.WriteByte(0); // no translated keyword
            data.Write(Encoding.UTF8.GetBytes(entry.Text));
            WriteChunk(output, "iTXt", data.GetBuffer().AsSpan(0, (int)data.Length));
        }
        else
        {
            if (entry.Text.Any(c => c > '\u00ff'))
            {
                throw new ArgumentException($"tEXt '{entry.Keyword}' must be Latin-1; use Utf8.", nameof(entry));
            }

            data.Write(Encoding.Latin1.GetBytes(entry.Text));
            WriteChunk(output, "tEXt", data.GetBuffer().AsSpan(0, (int)data.Length));
        }
    }

    /// <summary>Reads width/height from a PNG header without decoding the image.</summary>
    public static bool TryReadSize(ReadOnlySpan<byte> png, out int width, out int height)
    {
        width = height = 0;
        if (png.Length < 24 || !png[..8].SequenceEqual(Signature) || !png.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32BigEndian(png[16..]);
        height = BinaryPrimitives.ReadInt32BigEndian(png[20..]);
        return width > 0 && height > 0;
    }

    internal static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in type)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(buffer, Crc32(typeBytes, data));
        output.Write(buffer);
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
