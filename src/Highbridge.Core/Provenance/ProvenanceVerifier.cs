using System.Text.Json;
using Highbridge.Imaging;

namespace Highbridge.Provenance;

public sealed record ProvenanceCheck(bool Valid, PageProvenance? Provenance, string? ComputedSha256, string? Problem);

/// <summary>
/// Checks a stamped page: reads its embedded record, removes the band and recomputes the hash of
/// the scanned area. A match shows the page content is what the scanner returned. It does not
/// show who wrote the record; that needs a signature (see the provenance plan).
/// </summary>
public static class ProvenanceVerifier
{
    public static ProvenanceCheck Verify(ReadOnlySpan<byte> stampedPng)
    {
        var json = PngEncoder.ReadText(stampedPng).FirstOrDefault(t => t.Keyword == ProvenanceStamper.MetadataKeyword)?.Text;
        if (json is null)
        {
            return new ProvenanceCheck(false, null, null, "The PNG has no highbridge provenance record.");
        }

        PageProvenance? record;
        try
        {
            record = JsonSerializer.Deserialize<PageProvenance>(json, HighbridgeJson.Options);
        }
        catch (JsonException ex)
        {
            return new ProvenanceCheck(false, null, null, $"The provenance record is not valid: {ex.Message}");
        }

        if (record is null || record.HashMethod != PageProvenance.PixelHashMethod)
        {
            return new ProvenanceCheck(false, record, null, "Unknown provenance hash method.");
        }

        var image = PngDecoder.Decode(stampedPng);
        int contentRows = image.Height - record.BandHeight;
        if (record.BandHeight <= 0 || contentRows <= 0)
        {
            return new ProvenanceCheck(false, record, null, "The band height does not fit the image.");
        }

        int offset = record.Position == StampPosition.Top ? record.BandHeight : 0;
        var (hash, _) = ProvenanceStamper.HashPixels(image, contentRows, offset);
        bool match = string.Equals(hash, record.OriginalSha256, StringComparison.OrdinalIgnoreCase);
        return new ProvenanceCheck(match, record, hash, match ? null : "The page content does not match the recorded hash.");
    }
}
