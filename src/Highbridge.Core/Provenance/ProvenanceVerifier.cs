using System.Text.Json;
using Highbridge.Imaging;

namespace Highbridge.Provenance;

public sealed record ProvenanceCheck(bool Valid, PageProvenance? Provenance, string? ComputedSha256, string? Problem);

/// <summary>
/// Checks a stamped PNG: reads its embedded record and recomputes the hash of the image.
/// A match shows the pixels are what was stamped. It does not show who wrote the record.
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
        var (hash, _) = ProvenanceStamper.HashPixels(image, image.Height, rowOffset: 0);
        bool match = string.Equals(hash, record.OriginalSha256, StringComparison.OrdinalIgnoreCase);
        return new ProvenanceCheck(match, record, hash, match ? null : "The page content does not match the recorded hash.");
    }
}
