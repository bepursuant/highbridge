using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Highbridge.Imaging;

namespace Highbridge.Provenance;

/// <summary>The stamped file and the record written into it. Width and height are the original image.</summary>
public sealed record StampedImage(byte[] Png, int Width, int Height, PageProvenance Provenance);

/// <summary>
/// Writes the provenance record into a PNG as text chunks. The image chunks are copied unchanged.
/// </summary>
public static class ProvenanceStamper
{
    public const string MetadataKeyword = "highbridge:provenance";

    /// <param name="record">Everything except the hash, which is computed here (pass any placeholder).</param>
    public static StampedImage Stamp(ReadOnlySpan<byte> png, PageProvenance record)
    {
        var image = PngDecoder.Decode(png);
        var (hash, _) = HashPixels(image, image.Height, rowOffset: 0);
        var provenance = record with { OriginalSha256 = hash };
        byte[] output = PngEncoder.AddText(png, Metadata(provenance));
        return new StampedImage(output, image.Width, image.Height, provenance);
    }

    /// <summary>
    /// SHA-256 of <paramref name="rows"/> rows starting at <paramref name="rowOffset"/>, in the
    /// <see cref="PageProvenance.PixelHashMethod"/> form: the ASCII line "highbridge-pixels-v1\n",
    /// width and height (32-bit big-endian), the channel count (1 gray, 3 RGB), then every row as
    /// 8-bit samples. Also reports whether the area is pure black and white.
    /// </summary>
    internal static (string Sha256, bool Bilevel) HashPixels(PngImage image, int rows, int rowOffset)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.ASCII.GetBytes(PageProvenance.PixelHashMethod + "\n"));
        Span<byte> header = stackalloc byte[9];
        BinaryPrimitives.WriteInt32BigEndian(header, image.Width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], rows);
        header[8] = (byte)image.Channels;
        hash.AppendData(header);

        bool bilevel = image.IsGray;
        var row = new byte[image.Width * image.Channels];
        for (int y = 0; y < rows; y++)
        {
            image.ReadRow(rowOffset + y, row);
            hash.AppendData(row);
            if (bilevel && row.AsSpan().ContainsAnyExcept((byte)0, (byte)255))
            {
                bilevel = false;
            }
        }

        return (Convert.ToHexStringLower(hash.GetHashAndReset()), bilevel);
    }

    private static List<PngText> Metadata(PageProvenance p)
    {
        string description = string.Join('\n',
        [
            $"highbridge {p.ScanId}",
            $"page {p.Page} of {p.PageCount}",
            "scanned " + p.ScannedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
            $"workstation {p.Workstation}",
            $"user {p.User}",
            $"scanner {p.Scanner}",
            $"sha256 {p.OriginalSha256}",
        ]);
        return
        [
            new("Software", p.Software),
            new("Creation Time", p.ScannedAtUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            new("Source", p.Scanner, Utf8: true),
            new("Description", description, Utf8: true),
            new(MetadataKeyword, JsonSerializer.Serialize(p, HighbridgeJson.Options), Utf8: true),
        ];
    }
}
